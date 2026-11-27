/**
 * What the file pipeline learns of an image before it is stored (docs/03 §10.2, data model §6 `files`): its width and
 * height, and a thumbnail – a WebP of at most 32 KB, base64 – drawn through a canvas. Where the browser can't (no
 * `createImageBitmap`, a canvas that writes no WebP – older Safari versions – or bytes it can't decode) the image goes
 * without them: the thumbnail is an optimisation (map cards, lists), the image itself is shown from its bytes.
 */

export interface ImageInfo {
  width: number | null
  height: number | null
  /** WebP, base64 without a `data:` prefix; null when none could be made. */
  thumbnail: string | null
}

/** The images' part of the pipeline, replaceable in the tests (jsdom has no canvas). */
export interface ImageTools {
  describe(image: Blob): Promise<ImageInfo>
}

/** The most a thumbnail may have (data model §6). */
export const thumbnailMaxBytes = 32 * 1024

const none: ImageInfo = { width: null, height: null, thumbnail: null }

/** Bytes as base64 (btoa over chunks: a spread of 32 KB would overflow the call stack in some engines). */
export function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let i = 0; i < bytes.length; i += 0x2000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x2000))
  }
  return btoa(binary)
}

export function fromBase64(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes
}

function toBlob(canvas: HTMLCanvasElement, type: string, quality: number): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

/** The longer sides tried, then the qualities: the first WebP that fits wins. */
const sides = [480, 360, 240, 160]
const qualities = [0.8, 0.6, 0.4]

async function thumbnailOf(bitmap: ImageBitmap): Promise<string | null> {
  const longer = Math.max(bitmap.width, bitmap.height)
  if (!longer) return null
  for (const side of sides) {
    const scale = Math.min(1, side / longer)
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(bitmap.width * scale))
    canvas.height = Math.max(1, Math.round(bitmap.height * scale))
    const context = canvas.getContext('2d')
    if (!context) return null
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    for (const quality of qualities) {
      const blob = await toBlob(canvas, 'image/webp', quality)
      // A browser without a WebP encoder answers PNG: no thumbnail then.
      if (!blob || blob.type !== 'image/webp') return null
      if (blob.size <= thumbnailMaxBytes) return toBase64(new Uint8Array(await blob.arrayBuffer()))
    }
  }
  return null
}

/** The browser's canvas. */
export const canvasImages: ImageTools = {
  async describe(image) {
    if (typeof createImageBitmap !== 'function' || typeof document === 'undefined') return none
    let bitmap: ImageBitmap | null = null
    try {
      bitmap = await createImageBitmap(image, { imageOrientation: 'from-image' })
      const { width, height } = bitmap
      let thumbnail: string | null = null
      try {
        thumbnail = await thumbnailOf(bitmap)
      } catch {
        thumbnail = null
      }
      return { width, height, thumbnail }
    } catch {
      return none
    } finally {
      bitmap?.close()
    }
  },
}
