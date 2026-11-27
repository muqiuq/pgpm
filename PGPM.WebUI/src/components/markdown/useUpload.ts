import { ref } from 'vue'
import { extensionOf, fileSize } from './files'
import type { FileKind, FileSink, StoredFile } from './services'

/**
 * Uploads from the editors (docs/03 §18.4; PNMTD docs/locations-knowledge-plan.md §5.5), the browser's part:
 *
 * 1. What becomes an image: a pasted or dropped PNG, JPEG, GIF or WebP, or one picked with *Image*.
 *    Everything else, and anything picked with *Attach file*, is an attachment.
 * 2. A JPEG with metadata is drawn again through a canvas: its orientation applied, its metadata gone.
 * 3. An image over 2 MB is recompressed (WebP where the browser encodes it, JPEG otherwise; 90 %, then
 *    80 %), then scaled to 3,840 px on its longer side, and the person is told. An animated GIF isn't
 *    (it would lose its animation): it is offered as an attachment.
 * 4. PGPM: the prepared file goes to the provided FileSink (services.ts), which stores it and gives its URL. PNMTD's
 *    XMLHttpRequest POST is gone; phase 3's sink is the offline file pipeline (docs/03 §10.2). Without a sink, files
 *    are refused (`no_storage`).
 *
 * The server checks everything again; these limits are its defaults (docs/03 §10.1: Files:MaxImageBytes,
 * Files:MaxAttachmentBytes).
 */

export type { FileKind } from './services'

/** How a file arrived: pasted or dropped, picked with *Image*, or with *Attach file*. */
export type UploadIntent = 'paste' | 'image' | 'attach'

/** A stored file as the editors insert it: what the sink answered, and what it became. */
export type UploadedFile = StoredFile & { kind: FileKind }

/** What the editors call: the uploaded file, or null when it failed (and the person was told). */
export type UploadHandler = (
  file: File,
  intent: UploadIntent,
  onProgress?: (fraction: number) => void,
) => Promise<UploadedFile | null>

export const limits = {
  /** Files:MaxImageBytes, 2 MiB. */
  image: 2 * 1024 * 1024,
  /** Files:MaxAttachmentBytes, 25 MiB. */
  attachment: 25 * 1024 * 1024,
  /** The longer side a recompressed image is scaled to. */
  maxSide: 3840,
}

/** The image types PGPM shows (§5.5): checked by their bytes on the server. */
export const imageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']
const imageExtensions = ['png', 'jpg', 'jpeg', 'gif', 'webp']

/** Why a file wasn't stored. A FileSink may throw an UploadError with its own reason too (`blocked_type`, …). */
export type UploadFailure = 'too_large' | 'not_an_image' | 'blocked_type' | 'animated_gif' | 'no_storage' | 'failed'

export class UploadError extends Error {
  readonly reason: UploadFailure
  /** The file could go up as an attachment instead (an image too large, or not one PGPM shows). */
  readonly attachInstead: boolean

  constructor(reason: UploadFailure, message: string, attachInstead = false) {
    super(message)
    this.name = 'UploadError'
    this.reason = reason
    this.attachInstead = attachInstead
  }
}

function isImageType(file: Blob & { name?: string }): boolean {
  if (file.type) {
    return imageTypes.includes(file.type.toLowerCase())
  }
  return imageExtensions.includes(extensionOf(file.name ?? ''))
}

/** §5.5, 1.: an image if it is one of the four types, unless it was picked with *Attach file*. */
export function chooseKind(file: File, intent: UploadIntent): FileKind {
  if (intent === 'attach') {
    return 'attachment'
  }
  return isImageType(file) ? 'image' : 'attachment'
}

// --- bytes ------------------------------------------------------------------------------------

async function head(file: Blob, length: number): Promise<Uint8Array> {
  return new Uint8Array(await file.slice(0, length).arrayBuffer())
}

/** A JPEG's EXIF or XMP (APP1), IPTC (APP13) or comment, found before its image data (§5.5, 2.). */
export async function jpegHasMetadata(file: Blob): Promise<boolean> {
  const bytes = await head(file, 256 * 1024)
  if (bytes[0] !== 0xff || bytes[1] !== 0xd8) {
    return false
  }
  let i = 2
  while (i + 4 <= bytes.length) {
    if (bytes[i] !== 0xff) {
      return false
    }
    const marker = bytes[i + 1]!
    if (marker === 0xd9 || marker === 0xda) {
      return false
    }
    if (marker === 0xe1 || marker === 0xed || marker === 0xfe) {
      return true
    }
    const length = (bytes[i + 2]! << 8) | bytes[i + 3]!
    i += 2 + length
  }
  return false
}

/** Whether a GIF has more than one frame (§5.5, 3.). */
export async function gifIsAnimated(file: Blob): Promise<boolean> {
  const bytes = new Uint8Array(await file.arrayBuffer())
  if (bytes.length < 13 || String.fromCharCode(...bytes.subarray(0, 3)) !== 'GIF') {
    return false
  }
  let i = 13
  if (bytes[10]! & 0x80) {
    i += 3 * 2 ** ((bytes[10]! & 0x07) + 1)
  }
  let frames = 0
  const skipSubBlocks = () => {
    while (i < bytes.length && bytes[i] !== 0) {
      i += bytes[i]! + 1
    }
    i++
  }
  while (i < bytes.length) {
    const block = bytes[i]
    if (block === 0x2c) {
      frames++
      if (frames > 1) {
        return true
      }
      const packed = bytes[i + 9]!
      i += 10
      if (packed & 0x80) {
        i += 3 * 2 ** ((packed & 0x07) + 1)
      }
      i++ // LZW minimum code size
      skipSubBlocks()
    } else if (block === 0x21) {
      i += 2
      skipSubBlocks()
    } else {
      break
    }
  }
  return false
}

// --- the canvas ---------------------------------------------------------------------------------

function encode(
  bitmap: ImageBitmap,
  width: number,
  height: number,
  type: string,
  quality: number,
): Promise<Blob | null> {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) {
    return Promise.resolve(null)
  }
  if (type === 'image/jpeg') {
    // JPEG has no transparency: white behind it, not black.
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, width, height)
  }
  context.drawImage(bitmap, 0, 0, width, height)
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

let webp: Promise<boolean> | null = null

/** Whether this browser's canvas writes WebP (Safari writes PNG instead). */
function canEncodeWebp(): Promise<boolean> {
  webp ??= new Promise((resolve) => {
    const canvas = document.createElement('canvas')
    canvas.width = canvas.height = 1
    canvas.toBlob((blob) => resolve(blob?.type === 'image/webp'), 'image/webp', 0.8)
  })
  return webp
}

/** Only for tests: forget what the browser could encode. */
export function resetEncoderCheck() {
  webp = null
}

function renamed(name: string, extension: string): string {
  const dot = name.lastIndexOf('.')
  return `${dot > 0 ? name.slice(0, dot) : name || 'image'}.${extension}`
}

export interface PreparedFile {
  blob: Blob
  name: string
  kind: FileKind
  /** What the browser did, to tell the person: *Screenshot of 3.4 MB stored as 1.1 MB WebP*. */
  notice: string | null
  /** The file as it was, for *Attach the original instead*. */
  original: File | null
}

/**
 * The file as it goes up (§5.5, 1.–3.). Throws an UploadError when it can't: too large, or (with the
 * *Image* button) not an image PGPM shows.
 */
export async function prepareFile(file: File, kind: FileKind): Promise<PreparedFile> {
  const name = file.name || (kind === 'image' ? 'pasted-image.png' : 'file')
  if (kind === 'attachment') {
    if (file.size > limits.attachment) {
      throw new UploadError(
        'too_large',
        `${name} is ${fileSize(file.size)}: attachments are at most ${fileSize(limits.attachment)}.`,
      )
    }
    return { blob: file, name, kind, notice: null, original: null }
  }

  if (!isImageType(file)) {
    throw new UploadError(
      'not_an_image',
      `${name} isn’t an image PGPM shows (PNG, JPEG, GIF or WebP). It can be attached as a file.`,
      true,
    )
  }

  const type = file.type.toLowerCase() || `image/${extensionOf(name).replace('jpg', 'jpeg')}`
  let blob: Blob = file
  let bitmap: ImageBitmap | null = null
  const draw = async () => (bitmap ??= await createImageBitmap(file, { imageOrientation: 'from-image' }))

  if (type === 'image/jpeg' && typeof createImageBitmap === 'function' && (await jpegHasMetadata(file))) {
    const image = await draw()
    blob = (await encode(image, image.width, image.height, 'image/jpeg', 0.92)) ?? file
  }

  if (blob.size <= limits.image) {
    return { blob, name, kind, notice: null, original: null }
  }

  const tooLarge = `${name} is ${fileSize(file.size)}: images are at most ${fileSize(limits.image)}.`
  if (type === 'image/gif' && (await gifIsAnimated(file))) {
    throw new UploadError('animated_gif', `${tooLarge} An animated GIF isn't made smaller; attach it as a file.`, true)
  }
  if (typeof createImageBitmap !== 'function') {
    throw new UploadError('too_large', `${tooLarge} This browser can’t make it smaller; attach it as a file.`, true)
  }

  const image = await draw()
  const target = (await canEncodeWebp()) ? { type: 'image/webp', label: 'WebP', extension: 'webp' } : null
  const format = target ?? { type: 'image/jpeg', label: 'JPEG', extension: 'jpg' }
  const longer = Math.max(image.width, image.height)
  const sizes = [{ width: image.width, height: image.height }]
  if (longer > limits.maxSide) {
    const scale = limits.maxSide / longer
    sizes.push({ width: Math.round(image.width * scale), height: Math.round(image.height * scale) })
  }
  for (const size of sizes) {
    for (const quality of [0.9, 0.8]) {
      const smaller = await encode(image, size.width, size.height, format.type, quality)
      if (smaller && smaller.size <= limits.image) {
        const what = file.name ? name : 'Pasted image'
        return {
          blob: smaller,
          name: renamed(name, format.extension),
          kind,
          notice: `${what} of ${fileSize(file.size)} stored as ${fileSize(smaller.size)} ${format.label}`,
          original: file,
        }
      }
    }
  }
  throw new UploadError('too_large', `${tooLarge} Even made smaller it is too large; attach it as a file.`, true)
}

// --- the upload ---------------------------------------------------------------------------------

/** What the editor says when no FileSink is provided (docs/03 §18.4). */
export const noStorageMessage = 'Files can’t be added here yet: this text has no file storage.'

export interface UploadResult {
  file: UploadedFile
  /** What the browser did to the image, if anything (§5.5, 3.). */
  notice: string | null
  /** The file as it was, for *Attach the original instead*. */
  original: File | null
}

/** The prepared bytes as a File with the name they go up under. */
function asFile(prepared: PreparedFile): File {
  const { blob, name } = prepared
  return blob instanceof File && blob.name === name ? blob : new File([blob], name, { type: blob.type })
}

/**
 * The editors' uploads: `upload(file, kind)` prepares one file (steps 1–3) and hands it to `sink` (step 4); `active`
 * lists those in flight with their progress, for a status line. Resolves null when the sink declined (it told the
 * person); throws an UploadError when the file can't go (too large, not an image, no sink).
 */
export function useUpload(sink: FileSink | null) {
  const active = ref<{ id: number; name: string; progress: number }[]>([])
  let next = 0

  async function upload(
    file: File,
    kind: FileKind,
    onProgress?: (fraction: number) => void,
  ): Promise<UploadResult | null> {
    if (!sink) {
      throw new UploadError('no_storage', noStorageMessage)
    }
    const prepared = await prepareFile(file, kind)
    const entry = { id: ++next, name: prepared.name, progress: 0 }
    active.value = [...active.value, entry]
    try {
      const stored = await sink.store(asFile(prepared), prepared.kind, (fraction) => {
        active.value = active.value.map((a) => (a.id === entry.id ? { ...a, progress: fraction } : a))
        onProgress?.(fraction)
      })
      if (!stored || typeof stored.url !== 'string') {
        return null
      }
      return { file: { ...stored, kind: prepared.kind }, notice: prepared.notice, original: prepared.original }
    } finally {
      active.value = active.value.filter((a) => a.id !== entry.id)
    }
  }

  return { upload, active, chooseKind }
}
