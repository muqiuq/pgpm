import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import type { FileTransport } from '@/api/files'
import { UploadError } from '@/components/markdown/useUpload'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { memoryDb, until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import type { PgpmDb } from './db'
import { FilePipeline, NotDownloaded, isBlocked, sha256Hex, type ObjectUrls } from './files'
import type { ImageTools } from './images'
import { Repo } from './repo'
import type { FileRow, ItemRow } from './rows'

const ANA = '01970000-0000-7000-8000-00000000a001'
const BEN = '01970000-0000-7000-8000-00000000b001'
const G1 = '01970000-0000-7000-8000-000000000001'

const images: ImageTools = { describe: async () => ({ width: 4, height: 3, thumbnail: 'AAAA' }) }

function fakeUrls(): ObjectUrls & { revoked: string[] } {
  let n = 0
  const revoked: string[] = []
  return { create: () => `blob:test/${++n}`, revoke: (url) => void revoked.push(url), revoked }
}

const bytes = (n: number, seed = 1) => Uint8Array.from({ length: n }, (_, i) => (i * 7 + seed) % 251)

/**
 * A PNG as the server checks it by its bytes (signature, IHDR with the size, IDAT, IEND – nothing is decoded), with a
 * tEXt chunk when `text` is given: metadata the server strips at the commit.
 */
function png(seed = 1, text?: string): Uint8Array<ArrayBuffer> {
  const chunk = (type: string, data: number[]) => {
    const length = [24, 16, 8, 0].map((shift) => (data.length >>> shift) & 0xff)
    return [...length, ...[...type].map((c) => c.charCodeAt(0)), ...data, 0, 0, 0, 0]
  }
  return Uint8Array.from([
    ...[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a],
    ...chunk('IHDR', [0, 0, 0, 4, 0, 0, 0, 3, 8, 2, 0, 0, 0]),
    ...(text
      ? chunk(
          'tEXt',
          [...text].map((c) => c.charCodeAt(0)),
        )
      : []),
    ...chunk('IDAT', [0x78, 0x9c, seed & 0xff, 0x60, 0, 0, 0, 2, 0, 1]),
    ...chunk('IEND', []),
  ])
}
const pngFile = (name: string, seed = 1, text?: string) => new File([png(seed, text)], name, { type: 'image/png' })
const fileOf = (n: number, name = 'report.pdf', type = 'application/pdf', seed = 1) =>
  new File([bytes(n, seed)], name, { type })

/** The fake server's file calls as a transport, without fetch (another device's). */
function direct(server: FakeSyncServer, userId: string, counter?: { downloads: number }): FileTransport {
  return {
    putChunk: async (id, index, part) => server.putChunk(userId, id, index, new Uint8Array(part)),
    commit: (id, body) => server.commit(userId, id, body),
    download: async (id) => {
      if (counter) counter.downloads++
      const file = server.download(userId, id)
      return { bytes: file.bytes.slice().buffer, type: file.type }
    },
  }
}

describe('the file pipeline (docs/03 §10.2) against the server’s contract', () => {
  let server: FakeSyncServer
  let ana: TestClient
  let ben: TestClient
  let anaFiles: FilePipeline
  let benFiles: FilePipeline
  let notices: string[]
  /** Ana's network drops after this many chunk uploads (null: never), counted in `chunks`. */
  let dropAfterChunks: number | null
  let chunks: number
  const dropAfter = (n: number | null) => {
    dropAfterChunks = n
    chunks = 0
  }

  beforeEach(() => {
    server = new FakeSyncServer()
    server.addUser({ id: ANA, name: 'Ana Muster', groupIds: [G1] })
    server.addUser({ id: BEN, name: 'Ben Beispiel', groupIds: [G1] })
    server.addGroup(G1, 'Innovation team')
    ana = testClient(server, ANA, { live: false })
    ben = testClient(server, BEN, { live: false })
    notices = []
    dropAfterChunks = null
    const anaFetch = server.fetchFor(ANA)
    chunks = 0
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      if (!ana.network) throw new TypeError('Failed to fetch')
      const response = await anaFetch(input, init)
      if (/\/chunks\//.test(String(input)) && response.ok && dropAfterChunks !== null && ++chunks >= dropAfterChunks) {
        ana.network = false
      }
      return response
    })
    anaFiles = new FilePipeline({
      db: () => ana.db,
      repo: ana.repo,
      online: () => ana.network,
      images,
      objectUrls: fakeUrls(),
      chunkSize: 4,
      notify: (n) => notices.push(`${n.summary}: ${n.detail}`),
    })
    benFiles = new FilePipeline({
      db: () => ben.db,
      repo: ben.repo,
      transport: direct(server, BEN),
      online: () => ben.network,
      images,
      objectUrls: fakeUrls(),
      chunkSize: 4,
    })
  })

  afterEach(async () => {
    vi.unstubAllGlobals()
    for (const c of [ana, ben]) {
      await c.engine.stop()
      await c.db.delete()
    }
  })

  /** A note of Ana's shared with the team, on the server. */
  async function note() {
    const created = await ana.repo.transaction(async (tx) => {
      const n = await tx.create<ItemRow>('items', { type: 'note', title: 'With files' })
      await tx.create('item_shares', { itemId: n.id, groupId: G1, access: 'edit' })
      return n
    })
    expect(await ana.engine.sync({ pull: true })).toMatchObject({ ok: true })
    return created
  }

  const chunkCalls = (id: string) =>
    server.calls
      .filter((c) => c.includes(`/files/${id}/`))
      .map((c) => c.replace(/^\w+ /, '').replace(`/api/v1/files/${id}/`, ''))

  it('attaches in one transaction: the row (pending, sha256, thumbnail) and the bytes, one outbox entry', async () => {
    const n = await note()
    const row = await anaFiles.attachFile({ itemId: n.id }, fileOf(10), 'attachment')
    const outbox = await ana.db.outbox.toArray()
    expect(outbox.map((e) => [e.table, e.op])).toEqual([['files', 'insert']])
    expect(outbox[0]!.row).not.toHaveProperty('status')
    expect(row).toMatchObject({
      itemId: n.id,
      mapId: null,
      purpose: 'attachment',
      fileName: 'report.pdf',
      contentType: 'application/pdf',
      sizeBytes: 10,
      sha256: await sha256Hex(bytes(10).buffer),
      status: 'pending',
      thumbnail: null,
    })
    expect(await ana.db.blobs.get(row.id)).toMatchObject({ origin: 'local', uploaded: false, size: 10 })
    const image = await anaFiles.attachFile({ itemId: n.id }, fileOf(8, 'shot.png', 'image/png'), 'embedded')
    expect(image).toMatchObject({ width: 4, height: 3, thumbnail: 'AAAA', contentType: 'image/png' })
  })

  it('uploads after the insert reached the server, chunk by chunk, commits, and the other device downloads it', async () => {
    const n = await note()
    const row = await anaFiles.attachFile({ itemId: n.id }, fileOf(10), 'attachment')
    // The server doesn't know the row yet: nothing goes.
    expect(await anaFiles.flushPendingUploads()).toEqual({ uploaded: 0, failed: 0, stopped: null })
    await ana.engine.sync({ pull: true })
    expect(await anaFiles.flushPendingUploads()).toEqual({ uploaded: 1, failed: 0, stopped: null })
    expect(chunkCalls(row.id)).toEqual(['chunks/0', 'chunks/1', 'chunks/2', 'commit'])
    expect(await ana.db.synced('files').get(row.id)).toMatchObject({ status: 'available' })
    expect(await ana.db.blobs.get(row.id)).toMatchObject({ uploaded: true })
    expect(server.fileBytes.get(row.id)?.bytes).toEqual(bytes(10))

    await ben.engine.sync({ pull: true })
    expect(await ben.db.synced('files').get(row.id)).toMatchObject({ status: 'available', sizeBytes: 10 })
    const blob = await benFiles.fileBlob(row.id)
    expect(new Uint8Array(await blob.arrayBuffer())).toEqual(bytes(10))
    expect(await ben.db.blobs.get(row.id)).toMatchObject({ origin: 'download', uploaded: true })
  })

  it('resumes an interrupted upload at the next chunk, and starts over once when the server lost it (409)', async () => {
    const n = await note()
    const row = await anaFiles.attachFile({ itemId: n.id }, fileOf(14), 'attachment')
    await ana.engine.sync({ pull: true })
    dropAfter(2)
    expect((await anaFiles.flushPendingUploads()).stopped).toBe('offline')
    expect(await ana.db.blobs.get(row.id)).toMatchObject({ nextChunk: 2, uploaded: false })
    expect(await ana.db.synced('files').get(row.id)).toMatchObject({ status: 'pending' })

    dropAfter(null)
    ana.network = true
    // Dexie's update would have turned the bytes into a plain object (patchBlob keeps them).
    expect((await ana.db.blobs.get(row.id))!.bytes.byteLength).toBe(14)
    expect(await anaFiles.flushPendingUploads()).toEqual({ uploaded: 1, failed: 0, stopped: null })
    expect(chunkCalls(row.id)).toEqual(['chunks/0', 'chunks/1', 'chunks/2', 'chunks/3', 'commit'])

    // A second file whose upload the server forgot meanwhile (stale, restarted): chunk 2 is refused, 0 again.
    const other = await anaFiles.attachFile({ itemId: n.id }, fileOf(10, 'b.pdf', 'application/pdf', 3), 'attachment')
    await ana.engine.sync({ pull: true })
    dropAfter(2)
    await anaFiles.flushPendingUploads()
    server.uploads.delete(other.id)
    dropAfter(null)
    ana.network = true
    expect((await anaFiles.flushPendingUploads()).uploaded).toBe(1)
    expect(chunkCalls(other.id)).toEqual([
      'chunks/0',
      'chunks/1',
      'chunks/2',
      'chunks/0',
      'chunks/1',
      'chunks/2',
      'commit',
    ])
    expect(server.fileBytes.get(other.id)?.bytes).toEqual(bytes(10, 3))
  })

  it('keeps a refused upload as failed (with the server’s reason) until Retry', async () => {
    const n = await note()
    const row = await anaFiles.attachFile({ itemId: n.id }, fileOf(6), 'attachment')
    await ana.engine.sync({ pull: true })
    const serverRow = server.table('files').get(row.id)!
    const sha = serverRow.sha256
    serverRow.sha256 = 'damaged'
    expect(await anaFiles.flushPendingUploads()).toMatchObject({ uploaded: 0, failed: 1 })
    const damaged = 'The bytes received have another SHA-256 than the commit or the file says.'
    expect(await ana.db.blobs.get(row.id)).toMatchObject({ error: damaged, uploaded: false })
    expect(notices).toEqual([`Upload failed: report.pdf: ${damaged}`])
    const stored = (await ana.db.synced('files').get(row.id)) as unknown as FileRow
    expect((await anaFiles.states([stored])).get(row.id)).toEqual({ kind: 'failed', error: damaged })
    // Not retried by itself.
    expect((await anaFiles.flushPendingUploads()).uploaded).toBe(0)

    serverRow.sha256 = sha
    expect((await anaFiles.retry(row.id)).uploaded).toBe(1)
    expect(await ana.db.blobs.get(row.id)).toMatchObject({ error: null, uploaded: true })
  })

  it('downloads on demand once, checks the sha256, and offline says NotDownloaded', async () => {
    const n = await note()
    const row = await anaFiles.attachFile({ itemId: n.id }, fileOf(9), 'attachment')
    await ana.engine.sync({ pull: true })
    await anaFiles.flushPendingUploads()
    await ben.engine.sync({ pull: true })

    ben.network = false
    await expect(benFiles.fileBlob(row.id)).rejects.toBeInstanceOf(NotDownloaded)
    ben.network = true

    const stored = server.fileBytes.get(row.id)!
    stored.bytes[0] = stored.bytes[0]! ^ 1
    await expect(benFiles.fileBlob(row.id)).rejects.toThrow(/damaged/)
    expect(await ben.db.blobs.get(row.id)).toBeUndefined()
    stored.bytes[0] = stored.bytes[0]! ^ 1

    const counter = { downloads: 0 }
    const once = new FilePipeline({
      db: () => ben.db,
      repo: ben.repo,
      transport: direct(server, BEN, counter),
      objectUrls: fakeUrls(),
      online: () => true,
    })
    const [a, b] = await Promise.all([once.fileBlob(row.id), once.fileBlob(row.id)])
    expect(a.size).toBe(9)
    expect(b.size).toBe(9)
    await once.fileBlob(row.id)
    expect(counter.downloads).toBe(1)
  })

  it('refuses blocked types, too large attachments and non-images as covers before storing anything', async () => {
    const n = await note()
    expect(isBlocked('setup.exe')).toBe(true)
    expect(isBlocked('invoice.exe.pdf')).toBe(true)
    expect(isBlocked('notes.txt')).toBe(false)
    await expect(anaFiles.attachFile({ itemId: n.id }, fileOf(3, 'tool.EXE'), 'attachment')).rejects.toMatchObject({
      reason: 'blocked_type',
    })
    const big = new File([new Uint8Array(25 * 1024 * 1024 + 1)], 'big.zip', { type: 'application/zip' })
    await expect(anaFiles.attachFile({ itemId: n.id }, big, 'attachment')).rejects.toBeInstanceOf(UploadError)
    await expect(anaFiles.setCover(n.id, fileOf(3, 'a.txt', 'text/plain'))).rejects.toMatchObject({
      reason: 'not_an_image',
    })
    expect(await ana.db.outbox.count()).toBe(0)
    expect(await ana.db.blobs.count()).toBe(0)
  })

  it('sets, replaces and removes a cover with the item’s imageFileId in the same batch', async () => {
    const n = await note()
    const first = await anaFiles.setCover(n.id, pngFile('a.png'))
    const entries = await ana.db.outbox.toArray()
    expect(entries.map((e) => [e.table, e.op])).toEqual([
      ['files', 'insert'],
      ['items', 'patch'],
    ])
    expect(new Set(entries.map((e) => e.batch)).size).toBe(1)
    expect(await ana.db.synced('items').get(n.id)).toMatchObject({ imageFileId: first.file.id })

    // Replaced before it was synced: the first cover is deleted (the item's patch names it, so the insert stays).
    const second = await anaFiles.setCover(n.id, pngFile('b.png', 2))
    expect(await ana.db.synced('files').get(first.file.id)).toMatchObject({ deleted: true })
    expect(await ana.db.synced('items').get(n.id)).toMatchObject({ imageFileId: second.file.id })

    await ana.engine.sync({ pull: true })
    expect(server.table('items').get(n.id)).toMatchObject({ imageFileId: second.file.id })
    expect(server.table('files').get(first.file.id)).toMatchObject({ deleted: true })
    expect(await anaFiles.flushPendingUploads()).toMatchObject({ uploaded: 1 })
    expect(server.fileBytes.has(first.file.id)).toBe(false)
    expect(await ana.db.blobs.get(first.file.id)).toBeUndefined()

    await anaFiles.removeCover(n.id)
    expect(await ana.db.synced('items').get(n.id)).toMatchObject({ imageFileId: null })
    expect(await ana.db.synced('files').get(second.file.id)).toMatchObject({ deleted: true })
    await ana.engine.sync({ pull: true })
    expect(server.table('files').get(second.file.id)).toMatchObject({ deleted: true })
    await anaFiles.sweep()
    expect(await ana.db.blobs.get(second.file.id)).toBeUndefined()
  })

  it('takes the stored bytes of an image whose metadata the server stripped at the commit: row and device', async () => {
    const n = await note()
    const { file } = await anaFiles.setCover(n.id, pngFile('Screenshot', 1, 'Author\u0000Somebody'))
    expect(file).toMatchObject({ fileName: 'Screenshot', sizeBytes: png(1, 'Author\u0000Somebody').length })
    await ana.engine.sync({ pull: true })
    expect(await anaFiles.flushPendingUploads()).toMatchObject({ uploaded: 1 })
    const stored = server.fileBytes.get(file.id)!.bytes
    expect(stored).toEqual(png(1))
    // The commit's answer laid over the row (it isn't all of it): the item and purpose stay, the stored bytes' facts come.
    expect(await ana.db.synced('files').get(file.id)).toMatchObject({
      itemId: n.id,
      purpose: 'cover',
      status: 'available',
      fileName: 'Screenshot.png',
      sizeBytes: stored.length,
      sha256: await sha256Hex(stored.slice().buffer),
      width: 4,
      height: 3,
    })
    // The device holds what every other one gets.
    expect(new Uint8Array((await ana.db.blobs.get(file.id))!.bytes)).toEqual(stored)
    await ben.engine.sync({ pull: true })
    expect(new Uint8Array(await (await benFiles.fileBlob(file.id)).arrayBuffer())).toEqual(stored)
  })

  it('shares the bytes the server has already: the same file attached again is available at once, no upload', async () => {
    const n = await note()
    const first = await anaFiles.attachFile({ itemId: n.id }, fileOf(10), 'attachment')
    await ana.engine.sync({ pull: true })
    await anaFiles.flushPendingUploads()
    const again = await anaFiles.attachFile({ itemId: n.id }, fileOf(10, 'copy.pdf'), 'attachment')
    await ana.engine.sync({ pull: true })
    expect(await ana.db.synced('files').get(again.id)).toMatchObject({ status: 'available' })
    expect(await anaFiles.flushPendingUploads()).toEqual({ uploaded: 0, failed: 0, stopped: null })
    expect(chunkCalls(again.id)).toEqual([])
    expect(server.fileBytes.get(again.id)).toBe(server.fileBytes.get(first.id))
    // A chunk for a stored file (a commit whose answer was lost): 409 file_available, counted as uploaded.
    await ana.db.synced('files').update(again.id, { status: 'pending' })
    expect(await anaFiles.flushPendingUploads()).toEqual({ uploaded: 1, failed: 0, stopped: null })
    expect(chunkCalls(again.id)).toEqual(['chunks/0'])
  })

  it('attached offline, the file goes up with the first sync once the device is back online (afterRun)', async () => {
    const n = await note()
    let files: FilePipeline | null = null
    const device = testClient(server, ANA, { live: false, engine: { afterRun: () => files?.flushPendingUploads() } })
    const transport = direct(server, ANA)
    const guarded: FileTransport = {
      putChunk: (...args) =>
        device.network ? transport.putChunk(...args) : Promise.reject(new ApiError(0, undefined)),
      commit: (...args) => (device.network ? transport.commit(...args) : Promise.reject(new ApiError(0, undefined))),
      download: (...args) => transport.download(...args),
    }
    files = new FilePipeline({
      db: () => device.db,
      repo: device.repo,
      transport: guarded,
      online: () => device.network,
      images,
      objectUrls: fakeUrls(),
      chunkSize: 4,
    })
    try {
      device.engine.start()
      await until(async () => !!(await device.db.synced('items').get(n.id)), 'the note on the device')
      device.goOffline()
      const row = await files.attachFile({ itemId: n.id }, fileOf(6), 'attachment')
      await new Promise((r) => setTimeout(r, 30))
      expect(server.table('files').has(row.id)).toBe(false)
      device.goOnline()
      await until(async () => (await device.db.synced('files').get(row.id))?.status === 'available', 'the upload')
      expect(server.fileBytes.get(row.id)?.bytes).toEqual(bytes(6))
    } finally {
      await device.engine.stop()
      await device.db.delete()
    }
  })

  it('gives the editor a sink: images become embedded files, the URL of the file store', async () => {
    const n = await note()
    const stored = await anaFiles.sink(n.id).store(fileOf(4, 'shot.png', 'image/png'), 'image')
    expect(stored).toMatchObject({
      name: 'shot.png',
      size: 4,
      width: 4,
      height: 3,
      url: `/api/v1/files/${stored!.id}/shot.png`,
    })
    expect(await ana.db.synced('files').get(stored!.id)).toMatchObject({ purpose: 'embedded' })
    const doc = await anaFiles.sink(n.id).store(fileOf(4, 'a b.pdf'), 'attachment')
    expect(doc!.url).toBe(`/api/v1/files/${doc!.id}/a%20b.pdf`)
    expect(await ana.db.synced('files').get(doc!.id)).toMatchObject({ purpose: 'attachment' })

    // Removed before it was ever sent: the row, its outbox entry and its bytes go.
    await anaFiles.remove(doc!.id)
    expect(await ana.db.synced('files').get(doc!.id)).toBeUndefined()
    expect(await ana.db.blobs.get(doc!.id)).toBeUndefined()
    expect((await ana.db.outbox.toArray()).map((e) => e.id)).toEqual([stored!.id])
  })
})

describe('the cache, prefetch and URLs of the file pipeline', () => {
  let db: PgpmDb
  let files: FilePipeline
  let online: boolean
  let downloads: string[]
  const contents = new Map<string, Uint8Array>()

  async function put(
    id: string,
    options: { purpose?: string; status?: string; size?: number; cachedAt?: string | null; deleted?: boolean } = {},
  ) {
    const size = options.size ?? 10
    const data = bytes(size, id.length + id.charCodeAt(0))
    contents.set(id, data)
    await db.synced('files').put({
      id,
      itemId: 'item-1',
      mapId: null,
      purpose: options.purpose ?? 'attachment',
      fileName: `${id}.bin`,
      contentType: options.purpose && options.purpose !== 'attachment' ? 'image/png' : 'application/octet-stream',
      sizeBytes: size,
      sha256: await sha256Hex(data.buffer),
      status: options.status ?? 'available',
      thumbnail: null,
      deleted: options.deleted ?? false,
      rev: 1,
      seq: 1,
      createdAt: '2026-11-01T00:00:00Z',
      _local: { serverRev: 1, pending: false },
    })
    if (options.cachedAt) {
      await db.blobs.put({
        id,
        bytes: data.slice().buffer,
        type: 'application/octet-stream',
        size,
        cachedAt: options.cachedAt,
        origin: options.status === 'pending' ? 'local' : 'download',
        uploaded: options.status !== 'pending',
        purpose: options.purpose ?? 'attachment',
      })
    }
  }

  beforeEach(() => {
    db = memoryDb()
    online = true
    downloads = []
    files = new FilePipeline({
      db: () => db,
      repo: new Repo({ db: () => db, userId: () => 'me' }),
      online: () => online,
      objectUrls: fakeUrls(),
      budget: 25,
      transport: {
        putChunk: async () => ({ received: 0 }),
        commit: async () => ({}),
        download: async (id) => {
          if (!online) throw new TypeError('Failed to fetch')
          downloads.push(id)
          return { bytes: contents.get(id)!.slice().buffer, type: 'application/octet-stream' }
        },
      },
    })
  })

  afterEach(() => db.delete())

  it('evicts the oldest downloads first, attachments before prefetched images, never bytes not uploaded', async () => {
    await put('p1', { status: 'pending', cachedAt: '2026-11-01T00:00:00Z' })
    await put('c1', { purpose: 'cover', cachedAt: '2026-11-02T00:00:00Z' })
    await put('a1', { cachedAt: '2026-11-03T00:00:00Z' })
    await put('a2', { cachedAt: '2026-11-04T00:00:00Z' })
    expect(await files.usage()).toBe(40)
    expect(await files.enforceCacheBudget()).toBe(20)
    expect((await db.blobs.toCollection().primaryKeys()).sort()).toEqual(['c1', 'p1'])
    expect(await files.enforceCacheBudget({ budget: 5 })).toBe(10)
    expect(await db.blobs.toCollection().primaryKeys()).toEqual(['p1'])
  })

  it('prefetches covers, map images and images in texts – not attachments, not pending or deleted ones; stops offline', async () => {
    await put('cover', { purpose: 'cover' })
    await put('embedded', { purpose: 'embedded' })
    await put('attachment')
    await put('pending', { purpose: 'cover', status: 'pending' })
    await put('gone', { purpose: 'cover', deleted: true })
    online = false
    expect(await files.prefetch()).toBe(0)
    online = true
    expect(await files.prefetch()).toBe(2)
    expect(downloads.sort()).toEqual(['cover', 'embedded'])
    expect(await files.prefetch()).toBe(0)
  })

  it('gives blob: URLs of the bytes here or downloaded, the thumbnail when asked, null offline', async () => {
    await put('here', { cachedAt: '2026-11-01T00:00:00Z' })
    await put('pic', { purpose: 'embedded' })
    await put('pic2', { purpose: 'embedded', size: 5 })
    await db.synced('files').update('pic2', { thumbnail: 'AAAA' })
    const here = await files.fileUrl('here')
    expect(here).toMatch(/^blob:test\//)
    expect(await files.fileUrl('here')).toBe(here)
    expect(await files.fileUrl('pic')).toMatch(/^blob:test\//)
    expect(downloads).toEqual(['pic'])
    online = false
    expect(await files.fileUrl('pic2')).toBeNull()
    expect(await files.fileUrl('pic2', { thumbnail: true })).toMatch(/^blob:test\//)
    expect(await files.resolveFileUrl('https://example.com/x.png')).toBe('https://example.com/x.png')
    expect(await files.resolveFileUrl('/api/v1/files/not-a-uuid/x')).toBe('/api/v1/files/not-a-uuid/x')
  })
})

describe('resolveFileUrl on uuids', () => {
  it('answers by purpose and connection', async () => {
    const db = memoryDb()
    let online = true
    const urls = fakeUrls()
    const data = bytes(4)
    const sha = await sha256Hex(data.buffer)
    const files = new FilePipeline({
      db: () => db,
      repo: new Repo({ db: () => db, userId: () => 'me' }),
      online: () => online,
      objectUrls: urls,
      transport: {
        putChunk: async () => ({ received: 0 }),
        commit: async () => ({}),
        download: async () => ({ bytes: data.slice().buffer, type: 'image/png' }),
      },
    })
    const uuid = (n: number) => `01970000-0000-7000-8000-00000000000${n}`
    const row = (n: number, purpose: string) => ({
      id: uuid(n),
      itemId: 'i',
      purpose,
      fileName: 'f.png',
      contentType: 'image/png',
      sizeBytes: 4,
      sha256: sha,
      status: 'available',
      deleted: false,
      rev: 1,
      createdAt: '2026-11-01T00:00:00Z',
    })
    await db.synced('files').bulkPut([row(1, 'attachment'), row(2, 'embedded'), row(3, 'embedded')])
    const url = (n: number) => `/api/v1/files/${uuid(n)}/f.png`
    expect(await files.resolveFileUrl(url(1))).toBe(url(1))
    expect(await files.resolveFileUrl(url(2), { localOnly: true })).toBeNull()
    expect(await files.resolveFileUrl(url(2))).toMatch(/^blob:test\//)
    expect(await files.resolveFileUrl(url(2), { localOnly: true })).toMatch(/^blob:test\//)
    online = false
    expect(await files.resolveFileUrl(url(1))).toBeNull()
    expect(await files.resolveFileUrl(url(3))).toBeNull()
    await files.remove(uuid(2)).catch(() => undefined)
    await db.delete()
  })
})
