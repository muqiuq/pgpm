import { reactive, shallowRef, toValue, watch, type InjectionKey, type MaybeRefOrGetter, type Ref } from 'vue'
import { ApiError } from '@/api/client'
import { fileIdOf, filesApi, fileUrl, type FileTransport } from '@/api/files'
import { extensionOf, fileSize } from '@/components/markdown/files'
import type { FileSink, FileUrlResolver, StoredFile } from '@/components/markdown/services'
import type { MarkdownFile } from '@/components/markdown/types'
import { imageTypes, limits, prepareFile, UploadError } from '@/components/markdown/useUpload'
import { applyServerRow, syncScope } from './apply'
import { localDb, type BlobRow, type PgpmDb } from './db'
import { canvasImages, fromBase64, type ImageInfo, type ImageTools } from './images'
import type { Row } from './outbox'
import { metaOf, type Repo, type RepoTransaction } from './repo'
import type { FilePurpose, FileRow, ItemRow, Stored } from './rows'
import { useLive } from './useLive'

/**
 * The file pipeline (docs/03 §10.2, §18.4; PLM `files.js`): files work offline like any other data.
 *
 * - **Attach** (`attachFile`, `setCover`, the editor's `sink`): the client's checks (blocked types, 25 MB attachments,
 *   images prepared to ≤ 2 MB), sha256, width, height and thumbnail first; then **one** repository transaction writes
 *   the `files` row (`status: pending`, one outbox entry) and the bytes into IndexedDB `blobs`.
 * - **Upload** (`flushPendingUploads`, after every sync run that reached the server): the bytes of the rows the server
 *   has accepted, in chunks of 4 MiB, then the commit with sha256 and size; the committed row (`available`) is applied
 *   at once. The next chunk is kept after each one, so an interrupted upload resumes where it stopped; a chunk the
 *   server refuses as out of order (it lost the upload) starts over from chunk 0, once.
 * - **Download** (`fileBlob`) on demand, checked against the row's sha256 and cached; covers, map images and images in
 *   texts are **prefetched** (`prefetch`), so cards, maps and texts look complete offline.
 * - **Cache budget** (`enforceCacheBudget`, 200 MB): the oldest downloads go first – attachments before prefetched
 *   images – never bytes the server doesn't have yet.
 * - URLs for the UI (`fileUrl`, `resolveFileUrl`): blob: URLs of the local bytes (or the row's thumbnail).
 *
 * Sizes and order for the budget come from the `files` rows and the `cachedAt` index, so that no scan reads the bytes.
 */

export type FileOwner = { itemId: string; mapId?: undefined } | { mapId: string; itemId?: undefined }

/** Offline (or the server never had it) and not on this device: *Not available offline*. */
export class NotDownloaded extends Error {
  constructor(message = 'Not available offline.') {
    super(message)
    this.name = 'NotDownloaded'
  }
}

/** The file was deleted, or isn't shared with this user (any more). */
export class FileGone extends Error {
  constructor(message = 'The file is gone.') {
    super(message)
    this.name = 'FileGone'
  }
}

/** What runs for a file now: an upload (with its progress 0…1) or a download. */
export interface Transfer {
  phase: 'uploading' | 'downloading'
  progress: number
}

/** How a file stands on this device, for the attachment list. */
export type FileState =
  | { kind: 'local' }
  | { kind: 'pending' }
  | { kind: 'uploading'; progress: number }
  | { kind: 'failed'; error: string }
  | { kind: 'remote' }
  | { kind: 'elsewhere' }
  | { kind: 'downloading' }

export interface UploadSummary {
  uploaded: number
  failed: number
  /** Why the run stopped early: the network, the session, the server. */
  stopped: 'offline' | 'signed-out' | 'server' | null
}

export interface ObjectUrls {
  create(blob: Blob): string
  revoke(url: string): void
}

const browserUrls: ObjectUrls = {
  create: (blob) => URL.createObjectURL(blob),
  revoke: (url) => URL.revokeObjectURL(url),
}

export interface FilePipelineOptions {
  db?: () => PgpmDb
  repo: Repo
  transport?: FileTransport
  /** Whether the server can be reached (the browser and the sync say so). */
  online?: () => boolean
  images?: ImageTools
  objectUrls?: ObjectUrls
  /** The cache budget in bytes (200 MB). */
  budget?: number
  /** The chunk size (4 MiB, the server's limit). */
  chunkSize?: number
  /** A failed upload, once (a toast). */
  notify?: (notice: { severity: 'error' | 'warn' | 'info'; summary: string; detail?: string }) => void
}

/** Files:BlockedExtensions (docs/03 §10.1), checked here first. */
export const blockedExtensions = [
  'exe',
  'msi',
  'dll',
  'scr',
  'com',
  'bat',
  'cmd',
  'ps1',
  'vbs',
  'js',
  'jar',
  'hta',
  'lnk',
]

/** The purposes whose bytes are always downloaded (docs/03 §10.2, §18.4). */
const prefetched: ReadonlySet<FilePurpose> = new Set(['cover', 'map_image', 'embedded'])

/** The purposes that are images (prepared like PNMTD's, ≤ 2 MB). */
const imagePurposes: ReadonlySet<FilePurpose> = new Set(['cover', 'map_image', 'embedded'])

const contentTypes: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  gif: 'image/gif',
  webp: 'image/webp',
  pdf: 'application/pdf',
  txt: 'text/plain',
  md: 'text/markdown',
  csv: 'text/csv',
  json: 'application/json',
  zip: 'application/zip',
}

/** A file's type: as the browser says, else by its extension. */
export function contentTypeOf(file: { name?: string; type?: string }): string {
  return file.type || contentTypes[extensionOf(file.name ?? '')] || 'application/octet-stream'
}

/** Whether the name has a blocked extension – anywhere (`invoice.exe.pdf` too, docs/03 §10.1). */
export function isBlocked(name: string): boolean {
  return name
    .toLowerCase()
    .split('.')
    .slice(1)
    .some((part) => blockedExtensions.includes(part.trim()))
}

/** The bytes of a Blob (FileReader where `arrayBuffer` is missing). */
export function bytesOf(blob: Blob): Promise<ArrayBuffer> {
  if (typeof blob.arrayBuffer === 'function') return blob.arrayBuffer()
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as ArrayBuffer)
    reader.onerror = () => reject(reader.error ?? new Error('The file could not be read.'))
    reader.readAsArrayBuffer(blob)
  })
}

/** sha256, hex (the server verifies it, data model §6). */
export async function sha256Hex(bytes: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes)
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

/** A file ready to be stored: checked, prepared, hashed, described – everything that can't run in a transaction. */
export interface PreparedAttachment {
  id?: string
  name: string
  contentType: string
  purpose: FilePurpose
  bytes: ArrayBuffer
  sha256: string
  info: ImageInfo
  /** What the browser did to an image (*… stored as 1.1 MB WebP*), to tell the person. */
  notice: string | null
}

function isOffline(error: unknown): boolean {
  return (
    (error instanceof ApiError && error.status === 0) ||
    error instanceof TypeError ||
    (error instanceof DOMException && (error.name === 'AbortError' || error.name === 'TimeoutError'))
  )
}

const nowIso = () => new Date().toISOString()

/**
 * Changes members of a blob row. Never Dexie's `update`: its deep clone turns the ArrayBuffer into a plain object; a
 * get and a put keep it (IndexedDB's structured clone).
 */
async function patchBlob(db: PgpmDb, id: string, changes: Partial<BlobRow>): Promise<void> {
  await db.transaction('rw', db.blobs, async () => {
    const row = await db.blobs.get(id)
    if (row) await db.blobs.put({ ...row, ...changes })
  })
}

export class FilePipeline {
  /** Uploads and downloads running now, by file id. */
  readonly transfers: Record<string, Transfer> = reactive({})

  private readonly options: FilePipelineOptions
  private readonly transport: FileTransport
  private readonly images: ImageTools
  private readonly urls: ObjectUrls
  private readonly budget: number
  private readonly chunkSize: number
  private flushing: Promise<UploadSummary> | null = null
  private again = false
  private prefetching: Promise<number> | null = null
  private readonly downloads = new Map<string, Promise<Blob>>()
  private readonly objectUrls = new Map<string, string>()

  constructor(options: FilePipelineOptions) {
    this.options = options
    this.transport = options.transport ?? filesApi
    this.images = options.images ?? canvasImages
    this.urls = options.objectUrls ?? browserUrls
    this.budget = options.budget ?? 200 * 1024 * 1024
    this.chunkSize = options.chunkSize ?? 4 * 1024 * 1024
  }

  db(): PgpmDb {
    return (this.options.db ?? localDb)()
  }

  online(): boolean {
    return this.options.online?.() ?? (typeof navigator === 'undefined' || navigator.onLine !== false)
  }

  // --- attaching ----------------------------------------------------------------------------------------------------

  /**
   * Checks and prepares a file (docs/01 §4.5): blocked types refused, attachments ≤ 25 MB, images through PNMTD's
   * preparation (metadata gone, > 2 MB recompressed); then hashed and, for images, measured with a thumbnail. Throws an
   * UploadError for the person. `prepared`: the editor's useUpload did the preparation already.
   */
  async prepare(file: File, purpose: FilePurpose, options: { prepared?: boolean } = {}): Promise<PreparedAttachment> {
    const name = (file.name || (imagePurposes.has(purpose) ? 'image.png' : 'file')).trim()
    if (isBlocked(name)) {
      throw new UploadError('blocked_type', `${name}: files of this type can't be attached.`)
    }
    let blob: Blob = file
    let finalName = name
    let notice: string | null = null
    if (imagePurposes.has(purpose)) {
      if (!options.prepared) {
        const prepared = await prepareFile(file, 'image')
        blob = prepared.blob
        finalName = prepared.name
        notice = prepared.notice
      }
      const type = contentTypeOf({ name: finalName, type: blob.type })
      if (!imageTypes.includes(type)) {
        throw new UploadError('not_an_image', `${finalName} isn’t an image PGPM shows (PNG, JPEG, GIF or WebP).`, true)
      }
      if (blob.size > limits.image) {
        throw new UploadError(
          'too_large',
          `${finalName} is ${fileSize(blob.size)}: images are at most ${fileSize(limits.image)}.`,
          true,
        )
      }
    } else if (blob.size > limits.attachment) {
      throw new UploadError(
        'too_large',
        `${finalName} is ${fileSize(blob.size)}: attachments are at most ${fileSize(limits.attachment)}.`,
      )
    }
    const contentType = contentTypeOf({ name: finalName, type: blob.type })
    const bytes = await bytesOf(blob)
    const sha256 = await sha256Hex(bytes)
    const image = imageTypes.includes(contentType)
    const info = image ? await this.images.describe(new Blob([bytes], { type: contentType })) : null
    return {
      name: finalName,
      contentType,
      purpose,
      bytes,
      sha256,
      info: info ?? { width: null, height: null, thumbnail: null },
      notice,
    }
  }

  /** Inside a repository transaction: the `files` row and its bytes (one outbox entry). */
  async storeIn(tx: RepoTransaction, owner: FileOwner, prepared: PreparedAttachment): Promise<Stored<FileRow>> {
    const row = await tx.create<FileRow>('files', {
      ...(prepared.id ? { id: prepared.id } : {}),
      itemId: owner.itemId ?? null,
      mapId: owner.mapId ?? null,
      purpose: prepared.purpose,
      fileName: prepared.name,
      contentType: prepared.contentType,
      sizeBytes: prepared.bytes.byteLength,
      sha256: prepared.sha256,
      width: prepared.info.width,
      height: prepared.info.height,
      thumbnail: prepared.info.thumbnail,
    })
    const blob: BlobRow = {
      id: row.id,
      bytes: prepared.bytes,
      type: prepared.contentType,
      size: prepared.bytes.byteLength,
      cachedAt: nowIso(),
      origin: 'local',
      uploaded: false,
      purpose: prepared.purpose,
      error: null,
    }
    await tx.db.blobs.put(blob)
    return row as Stored<FileRow>
  }

  /** A file for an item or map, stored locally and queued for upload; works offline. */
  async attachFile(
    owner: FileOwner,
    file: File,
    purpose: FilePurpose,
    options: { prepared?: boolean } = {},
  ): Promise<Stored<FileRow>> {
    const prepared = await this.prepare(file, purpose, options)
    return this.options.repo.transaction((tx) => this.storeIn(tx, owner, prepared))
  }

  /**
   * A new cover image: the file and the item's `imageFileId` in one transaction. The previous cover's file is removed
   * when it belongs to this item and no other item on the device shows it (a project converted from an idea reuses the
   * idea's cover, docs/03 §12.8).
   */
  async setCover(itemId: string, file: File): Promise<{ file: Stored<FileRow>; notice: string | null }> {
    const prepared = await this.prepare(file, 'cover')
    const stored = await this.options.repo.transaction(async (tx) => {
      const item = (await tx.db.synced('items').get(itemId)) as Stored<ItemRow> | undefined
      const row = await this.storeIn(tx, { itemId }, prepared)
      await tx.patch('items', itemId, { imageFileId: row.id })
      if (item?.imageFileId) await this.dropCoverFile(tx, itemId, item.imageFileId)
      return row
    })
    return { file: stored, notice: prepared.notice }
  }

  /** No cover any more: `imageFileId` null; its file goes as in {@link setCover}. */
  async removeCover(itemId: string): Promise<void> {
    await this.options.repo.transaction(async (tx) => {
      const item = (await tx.db.synced('items').get(itemId)) as Stored<ItemRow> | undefined
      if (!item?.imageFileId) return
      await tx.patch('items', itemId, { imageFileId: null })
      await this.dropCoverFile(tx, itemId, item.imageFileId)
    })
  }

  private async dropCoverFile(tx: RepoTransaction, itemId: string, fileId: string) {
    const file = (await tx.db.synced('files').get(fileId)) as Stored<FileRow> | undefined
    if (!file || file.deleted || file.itemId !== itemId || file.purpose !== 'cover') return
    const others = await tx.db
      .synced('items')
      .filter((i) => i.id !== itemId && i.imageFileId === fileId && i.deleted !== true)
      .count()
    if (!others) await this.removeIn(tx, fileId)
  }

  /** Renames a file (`fileName` is patchable); the same checks as a new file's name. */
  async rename(id: string, name: string): Promise<void> {
    const next = name.trim()
    if (!next) throw new UploadError('failed', 'A name is needed.')
    if (next.length > 255) throw new UploadError('failed', 'At most 255 characters.')
    if (isBlocked(next)) throw new UploadError('blocked_type', `${next}: files of this type can't be attached.`)
    await this.options.repo.patch('files', id, { fileName: next })
  }

  /** Removes a file (needs *edit* on its item or map): a tombstone until the server confirms. */
  remove(id: string): Promise<void> {
    return this.options.repo.transaction((tx) => this.removeIn(tx, id))
  }

  private async removeIn(tx: RepoTransaction, id: string) {
    await tx.remove('files', id)
    const left = (await tx.db.synced('files').get(id)) as Stored<FileRow> | undefined
    // Never synced: gone with its bytes. On the server already: the bytes can come back from there (a restore).
    // Not uploaded yet but pushed: the bytes wait for the delete's answer (the sweep removes them then).
    if (!left || left.status === 'available') await tx.db.blobs.delete(id)
    this.forgetUrl(id)
  }

  /** The editor's FileSink (docs/03 §18.4): images become `embedded` files of the item, other files attachments. */
  sink(itemId: string): FileSink {
    return {
      store: async (file, kind, onProgress): Promise<StoredFile> => {
        const purpose: FilePurpose = kind === 'image' ? 'embedded' : 'attachment'
        const row = await this.attachFile({ itemId }, file, purpose, { prepared: true })
        onProgress?.(1)
        return {
          id: row.id,
          url: fileUrl(row.id, row.fileName),
          name: row.fileName,
          contentType: row.contentType,
          size: row.sizeBytes,
          ...(row.width !== null ? { width: row.width } : {}),
          ...(row.height !== null ? { height: row.height } : {}),
        }
      },
    }
  }

  // --- uploading ----------------------------------------------------------------------------------------------------

  /** *Retry* of a failed upload: the error goes, the upload starts over. */
  async retry(id: string): Promise<UploadSummary> {
    await patchBlob(this.db(), id, { error: null, nextChunk: 0 })
    return this.flushPendingUploads()
  }

  /** Uploads the bytes the server is waiting for. One run at a time; a call meanwhile runs once more afterwards. */
  flushPendingUploads(): Promise<UploadSummary> {
    if (this.flushing) {
      this.again = true
      return this.flushing
    }
    const run = (async () => {
      let summary: UploadSummary
      try {
        do {
          this.again = false
          summary = await this.flushOnce()
        } while (this.again && !summary.stopped)
      } finally {
        // Before the caller hears the answer: a call right after it starts a run of its own.
        this.flushing = null
      }
      return summary
    })()
    this.flushing = run
    return run
  }

  private async flushOnce(): Promise<UploadSummary> {
    const summary: UploadSummary = { uploaded: 0, failed: 0, stopped: null }
    const db = this.db()
    try {
      await this.sweep()
      const local = new Set(await db.blobs.toCollection().primaryKeys())
      const rows = (
        (await db
          .synced('files')
          .filter((r) => r.status === 'pending' && r.deleted !== true && local.has(r.id))
          .toArray()) as unknown as Stored<FileRow>[]
      )
        // The insert reached the server: it knows the row the bytes are for.
        .filter((r) => metaOf(r).serverRev !== null)
        .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
      for (const row of rows) {
        if (!this.online()) {
          summary.stopped = 'offline'
          break
        }
        const outcome = await this.upload(row)
        if (outcome === 'uploaded') summary.uploaded++
        else if (outcome === 'failed') summary.failed++
        else if (outcome !== 'skipped') {
          summary.stopped = outcome
          break
        }
      }
    } catch (e) {
      console.error('PGPM uploads failed', e)
      summary.stopped = 'server'
    }
    return summary
  }

  /** Removes bytes nobody needs: of rows that are gone, or deleted with the server's confirmation. */
  async sweep(): Promise<number> {
    const db = this.db()
    const ids = await db.blobs.toCollection().primaryKeys()
    const rows = (await db.synced('files').bulkGet(ids)) as (Stored<FileRow> | undefined)[]
    const orphans = ids.filter((_, i) => {
      const row = rows[i]
      return !row || (row.deleted === true && !row._local?.pending)
    })
    if (orphans.length) await db.blobs.bulkDelete(orphans)
    for (const id of orphans) this.forgetUrl(id)
    return orphans.length
  }

  private async upload(row: Stored<FileRow>): Promise<'uploaded' | 'failed' | 'skipped' | UploadSummary['stopped']> {
    const db = this.db()
    const blob = await db.blobs.get(row.id)
    if (!blob || blob.uploaded || blob.error) return 'skipped'
    const size = blob.bytes.byteLength
    const chunks = Math.max(1, Math.ceil(size / this.chunkSize))
    let next = Math.min(blob.nextChunk ?? 0, chunks)
    let restarted = false
    const progress = () => (this.transfers[row.id] = { phase: 'uploading', progress: Math.min(1, next / chunks) })
    const restart = async () => {
      restarted = true
      next = 0
      await patchBlob(db, row.id, { nextChunk: 0 })
    }
    progress()
    try {
      for (;;) {
        while (next < chunks) {
          // Copied through this page's Uint8Array: the stored buffer may come from another realm (IndexedDB's clone).
          const part = new Uint8Array(blob.bytes).slice(
            next * this.chunkSize,
            Math.min(size, (next + 1) * this.chunkSize),
          ).buffer
          let answer: { received: number }
          try {
            answer = await this.transport.putChunk(row.id, next, part)
          } catch (e) {
            if (e instanceof ApiError && e.status === 409 && next > 0 && !restarted) {
              await restart()
              continue
            }
            throw e
          }
          if (answer?.received !== Math.min(size, (next + 1) * this.chunkSize)) {
            if (next > 0 && !restarted) {
              await restart()
              continue
            }
            throw new ApiError(409, { code: 'upload_mismatch', detail: 'The server received other bytes than sent.' })
          }
          next++
          await patchBlob(db, row.id, { nextChunk: next })
          progress()
        }
        let committed: Row
        try {
          committed = await this.transport.commit(row.id, { sha256: row.sha256, size })
        } catch (e) {
          // The server lost the upload or got it damaged: once more from the start.
          if (e instanceof ApiError && (e.status === 409 || e.status === 422) && !restarted) {
            await restart()
            continue
          }
          throw e
        }
        await db.transaction('rw', syncScope(db), async () => {
          if (committed && typeof committed === 'object' && committed.id === row.id) {
            await applyServerRow(db, 'files', committed)
          }
          await patchBlob(db, row.id, { uploaded: true, nextChunk: 0, error: null, cachedAt: nowIso() })
        })
        return 'uploaded'
      }
    } catch (e) {
      return this.uploadFailed(row, e)
    } finally {
      delete this.transfers[row.id]
    }
  }

  private async uploadFailed(
    row: Stored<FileRow>,
    error: unknown,
  ): Promise<'uploaded' | 'failed' | 'skipped' | UploadSummary['stopped']> {
    if (isOffline(error)) return 'offline'
    if (!(error instanceof ApiError)) {
      console.error('PGPM upload failed', error)
      return 'server'
    }
    if (error.status === 401) return 'signed-out'
    // A commit whose answer was lost: the server has the bytes; the row's `available` comes with the pull.
    if (error.status === 409 && error.code === 'already_available') {
      await patchBlob(this.db(), row.id, { uploaded: true, nextChunk: 0, error: null })
      return 'uploaded'
    }
    // The row isn't on the server (yet, or any more): the next run sees.
    if (error.status === 404) return 'skipped'
    if (error.status >= 500 || error.status === 429 || error.status === 408) return 'server'
    const message = error.detail ?? error.message
    await patchBlob(this.db(), row.id, { error: message })
    this.options.notify?.({ severity: 'error', summary: 'Upload failed', detail: `${row.fileName}: ${message}` })
    return 'failed'
  }

  // --- downloading --------------------------------------------------------------------------------------------------

  /**
   * A file's bytes: from this device, else downloaded (online), checked against the row's sha256 and cached. Throws
   * NotDownloaded offline (or while the bytes are still on another device), FileGone for a deleted or unknown file.
   */
  async fileBlob(id: string): Promise<Blob> {
    const local = await this.db().blobs.get(id)
    if (local) return new Blob([local.bytes], { type: local.type })
    const running = this.downloads.get(id)
    if (running) return running
    const download = this.download(id, true).finally(() => this.downloads.delete(id))
    this.downloads.set(id, download)
    return download
  }

  private async download(id: string, enforce: boolean): Promise<Blob> {
    const db = this.db()
    const row = (await db.synced('files').get(id)) as Stored<FileRow> | undefined
    if (!row || row.deleted) throw new FileGone()
    if (row.status !== 'available') throw new NotDownloaded('Not uploaded yet: it is still on the device it came from.')
    if (!this.online()) throw new NotDownloaded()
    this.transfers[id] = { phase: 'downloading', progress: 0 }
    try {
      let data: { bytes: ArrayBuffer; type: string }
      try {
        data = await this.transport.download(id, row.fileName)
      } catch (e) {
        if (isOffline(e)) throw new NotDownloaded()
        if (e instanceof ApiError && (e.status === 404 || e.status === 403)) throw new FileGone()
        throw e
      }
      if ((await sha256Hex(data.bytes)) !== row.sha256) {
        throw new Error(`${row.fileName} arrived damaged (its checksum doesn't match).`)
      }
      const type = row.contentType || data.type
      await db.blobs.put({
        id,
        bytes: data.bytes,
        type,
        size: data.bytes.byteLength,
        cachedAt: nowIso(),
        origin: 'download',
        uploaded: true,
        purpose: row.purpose,
        error: null,
      })
      if (enforce) await this.enforceCacheBudget({ keep: [id] })
      return new Blob([data.bytes], { type })
    } finally {
      delete this.transfers[id]
    }
  }

  /** Bytes on this device and their rows, oldest first (the `cachedAt` index; the sizes from the rows). */
  private async cached(): Promise<{ id: string; row: Stored<FileRow> | undefined }[]> {
    const db = this.db()
    const ids = (await db.blobs.orderBy('cachedAt').primaryKeys()) as string[]
    const rows = (await db.synced('files').bulkGet(ids)) as (Stored<FileRow> | undefined)[]
    return ids.map((id, i) => ({ id, row: rows[i] }))
  }

  /** Bytes the cache holds now (unuploaded ones included). */
  async usage(): Promise<number> {
    return (await this.cached()).reduce((sum, c) => sum + (c.row?.sizeBytes ?? 0), 0)
  }

  /**
   * Keeps the cached bytes within the budget: downloads (and uploaded files) go oldest first – attachments before the
   * prefetched images – never bytes the server doesn't have. Answers the bytes evicted.
   */
  async enforceCacheBudget(
    options: { keep?: Iterable<string>; budget?: number; attachmentsOnly?: boolean } = {},
  ): Promise<number> {
    const budget = options.budget ?? this.budget
    const keep = new Set(options.keep ?? [])
    const cached = await this.cached()
    let usage = cached.reduce((sum, c) => sum + (c.row?.sizeBytes ?? 0), 0)
    if (usage <= budget) return 0
    const evictable = cached.filter(
      (c) => !keep.has(c.id) && c.row && !c.row.deleted && c.row.status === 'available' && !c.row._local?.pending,
    )
    const order = [
      ...evictable.filter((c) => !prefetched.has(c.row!.purpose)),
      ...(options.attachmentsOnly ? [] : evictable.filter((c) => prefetched.has(c.row!.purpose))),
    ]
    const gone: string[] = []
    let freed = 0
    for (const c of order) {
      if (usage <= budget) break
      gone.push(c.id)
      usage -= c.row!.sizeBytes
      freed += c.row!.sizeBytes
    }
    if (gone.length) await this.db().blobs.bulkDelete(gone)
    for (const id of gone) this.forgetUrl(id)
    return freed
  }

  /**
   * Downloads the covers, map images and images in texts this device doesn't have (docs/03 §10.2), one after the
   * other, while online and within the budget (it makes room by evicting attachments, never other prefetched images).
   * Answers how many it downloaded.
   */
  prefetch(): Promise<number> {
    if (this.prefetching) return this.prefetching
    const run = (async () => {
      try {
        return await this.prefetchOnce()
      } finally {
        this.prefetching = null
      }
    })()
    this.prefetching = run
    return run
  }

  private async prefetchOnce(): Promise<number> {
    const db = this.db()
    let count = 0
    try {
      const local = new Set(await db.blobs.toCollection().primaryKeys())
      const wanted = (
        (await db
          .synced('files')
          .filter(
            (r) =>
              r.deleted !== true &&
              r.status === 'available' &&
              prefetched.has(r.purpose as FilePurpose) &&
              !local.has(r.id),
          )
          .toArray()) as unknown as Stored<FileRow>[]
      ).sort((a, b) => b.createdAt.localeCompare(a.createdAt))
      for (const row of wanted) {
        if (!this.online()) break
        if ((await this.usage()) + row.sizeBytes > this.budget) {
          await this.enforceCacheBudget({ budget: this.budget - row.sizeBytes, attachmentsOnly: true })
          if ((await this.usage()) + row.sizeBytes > this.budget) break
        }
        if (this.downloads.has(row.id)) continue
        try {
          await this.download(row.id, false)
          count++
        } catch (e) {
          if (e instanceof NotDownloaded) break
          // A damaged or vanished file: the others still come.
        }
      }
    } catch (e) {
      console.error('PGPM prefetch failed', e)
    }
    return count
  }

  // --- URLs ---------------------------------------------------------------------------------------------------------

  private forgetUrl(id: string) {
    for (const key of [id, `thumb:${id}`]) {
      const url = this.objectUrls.get(key)
      if (url) {
        this.urls.revoke(url)
        this.objectUrls.delete(key)
      }
    }
  }

  private urlFor(key: string, blob: Blob): string {
    const known = this.objectUrls.get(key)
    if (known) return known
    const url = this.urls.create(blob)
    this.objectUrls.set(key, url)
    return url
  }

  /** The blob: URL of the bytes on this device, or null (nothing fetched). */
  async localUrl(id: string): Promise<string | null> {
    const known = this.objectUrls.get(id)
    if (known) return known
    const local = await this.db().blobs.get(id)
    return local ? this.urlFor(id, new Blob([local.bytes], { type: local.type })) : null
  }

  /**
   * A blob: URL to show a file: the row's thumbnail when asked for one (and the row has one), else the bytes – from
   * this device or downloaded (online). Null where it can't be shown.
   */
  async fileUrl(id: string, options: { thumbnail?: boolean } = {}): Promise<string | null> {
    if (options.thumbnail) {
      const known = this.objectUrls.get(`thumb:${id}`)
      if (known) return known
      const row = (await this.db().synced('files').get(id)) as Stored<FileRow> | undefined
      if (row?.thumbnail && !row.deleted) {
        try {
          return this.urlFor(`thumb:${id}`, new Blob([fromBase64(row.thumbnail)], { type: 'image/webp' }))
        } catch {
          // Not base64 after all: the image itself.
        }
      }
    }
    const local = await this.localUrl(id)
    if (local) return local
    try {
      return this.urlFor(id, await this.fileBlob(id))
    } catch {
      return null
    }
  }

  /**
   * The Markdown components' resolver (docs/03 §18.4, step 3): `/api/v1/files/{id}/…` → the local bytes' blob: URL;
   * images not here are downloaded (online), attachments are not (their link downloads from the server when clicked;
   * the chip resolves on sight). `localOnly` (compact texts): only what is here. Other URLs stay as they are.
   */
  readonly resolveFileUrl: FileUrlResolver = async (url, options = {}) => {
    const id = fileIdOf(url)
    if (!id) return url
    const local = await this.localUrl(id)
    if (local) return local
    if (options.localOnly) return null
    const row = (await this.db().synced('files').get(id)) as Stored<FileRow> | undefined
    if (!row || row.deleted || row.purpose === 'attachment') return this.online() ? url : null
    try {
      return this.urlFor(id, await this.fileBlob(id))
    } catch {
      return null
    }
  }

  /** After a wipe: no URL of the old data stays. */
  reset() {
    for (const url of this.objectUrls.values()) this.urls.revoke(url)
    this.objectUrls.clear()
    this.downloads.clear()
    for (const id of Object.keys(this.transfers)) delete this.transfers[id]
  }

  /** How each of the files stands on this device (the attachment list's tags). */
  async states(rows: readonly Stored<FileRow>[]): Promise<Map<string, FileState>> {
    const db = this.db()
    const ids = rows.map((r) => r.id)
    const local = new Set(await db.blobs.where('id').anyOf(ids).primaryKeys())
    const pendingHere = rows.filter((r) => r.status !== 'available' && local.has(r.id)).map((r) => r.id)
    const blobs = pendingHere.length ? await db.blobs.bulkGet(pendingHere) : []
    const errors = new Map(blobs.filter((b) => b?.error).map((b) => [b!.id, b!.error!]))
    const states = new Map<string, FileState>()
    for (const row of rows) {
      const transfer = this.transfers[row.id]
      if (transfer?.phase === 'uploading') states.set(row.id, { kind: 'uploading', progress: transfer.progress })
      else if (transfer?.phase === 'downloading') states.set(row.id, { kind: 'downloading' })
      else if (errors.has(row.id)) states.set(row.id, { kind: 'failed', error: errors.get(row.id)! })
      else if (row.status !== 'available') states.set(row.id, { kind: local.has(row.id) ? 'pending' : 'elsewhere' })
      else states.set(row.id, { kind: local.has(row.id) ? 'local' : 'remote' })
    }
    return states
  }
}

/** The pipeline the components use (provided in tests; the app's otherwise). */
export const filePipelineKey: InjectionKey<FilePipeline> = Symbol('pgpm.files')

/** A file row as the Markdown components know it (docs/03 §18.4). */
export function markdownFile(row: FileRow): MarkdownFile {
  return {
    id: row.id,
    name: row.fileName,
    size: row.sizeBytes,
    contentType: row.contentType,
    kind: row.purpose === 'attachment' ? 'attachment' : 'image',
    status: row.status,
    deleted: row.deleted === true,
  }
}

/** The files of an item, live, for the editor's and renderer's `files` (not deleted). */
export function useMarkdownFiles(
  itemId: MaybeRefOrGetter<string>,
  db: () => PgpmDb = localDb,
): Readonly<Ref<MarkdownFile[]>> {
  const live = useLive(
    async () =>
      ((await db().synced('files').where('itemId').equals(toValue(itemId)).toArray()) as unknown as FileRow[])
        .filter((r) => !r.deleted)
        .map(markdownFile),
    [] as MarkdownFile[],
    () => toValue(itemId),
  )
  return live.value
}

/** The files of an item or map, live, by purpose (not deleted), oldest first. */
export function useFileRows(
  owner: MaybeRefOrGetter<FileOwner>,
  purpose: FilePurpose | null,
  db: () => PgpmDb,
): Readonly<Ref<Stored<FileRow>[]>> {
  const live = useLive(
    async () => {
      const o = toValue(owner)
      const rows = (o.itemId
        ? await db().synced('files').where('itemId').equals(o.itemId).toArray()
        : await db().synced('files').where('mapId').equals(o.mapId!).toArray()) as unknown as Stored<FileRow>[]
      return rows
        .filter((r) => !r.deleted && (!purpose || r.purpose === purpose))
        .sort((a, b) => a.createdAt.localeCompare(b.createdAt) || (a.id < b.id ? -1 : 1))
    },
    [] as Stored<FileRow>[],
    () => JSON.stringify(toValue(owner)),
  )
  return live.value
}

/** The states of the files (the pipeline's transfers included), live. */
export function useFileStates(
  pipeline: FilePipeline,
  rows: Readonly<Ref<readonly Stored<FileRow>[]>>,
): Readonly<Ref<Map<string, FileState>>> {
  const states = shallowRef(new Map<string, FileState>())
  // A live query over the bytes: they arrive, go (evicted) or fail – and the rows' status changes.
  const live = useLive(
    () => pipeline.states(rows.value),
    new Map<string, FileState>(),
    () => rows.value.map((r) => `${r.id}:${r.status}:${r.rev}`).join(','),
  )
  watch(live.value, (next) => (states.value = next), { immediate: true })
  // Transfers are in memory: their progress, start and end.
  watch(
    () => JSON.stringify(pipeline.transfers),
    async () => (states.value = await pipeline.states(rows.value)),
  )
  return states
}
