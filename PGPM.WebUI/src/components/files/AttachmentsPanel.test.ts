import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase, type PgpmDb } from '@/local/db'
import { FilePipeline, filePipelineKey } from '@/local/files'
import { Repo } from '@/local/repo'
import { memoryDb, until } from '@/test/memoryDb'
import { mountWithUi } from '@/test/mount'
import AttachmentsPanel from './AttachmentsPanel.vue'

const ITEM = '01970000-0000-7000-8000-0000000000c1'

describe('AttachmentsPanel (docs/01 §4.5)', () => {
  let db: PgpmDb
  let files: FilePipeline
  let online: boolean

  const row = (id: string, extra: Record<string, unknown> = {}) => ({
    id,
    itemId: ITEM,
    mapId: null,
    purpose: 'attachment',
    fileName: `${id}.pdf`,
    contentType: 'application/pdf',
    sizeBytes: 2048,
    sha256: 'x',
    status: 'available',
    thumbnail: null,
    deleted: false,
    rev: 1,
    seq: 1,
    createdAt: '2026-11-20T10:00:00Z',
    createdBy: null,
    _local: { serverRev: 1, pending: false },
    ...extra,
  })

  const blob = (id: string, extra: Record<string, unknown> = {}) => ({
    id,
    bytes: new Uint8Array([1, 2, 3]).buffer,
    type: 'application/pdf',
    size: 3,
    cachedAt: '2026-11-20T10:00:00Z',
    origin: 'local' as const,
    uploaded: false,
    purpose: 'attachment',
    ...extra,
  })

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    online = false
    files = new FilePipeline({
      db: () => db,
      repo: new Repo({ db: () => db, userId: () => 'me' }),
      online: () => online,
      images: { describe: async () => ({ width: null, height: null, thumbnail: null }) },
      objectUrls: { create: () => 'blob:test/1', revoke: () => undefined },
      transport: {
        putChunk: async () => ({ received: 0 }),
        commit: async () => ({}),
        download: async () => ({ bytes: new ArrayBuffer(0), type: 'application/pdf' }),
      },
    })
    await db.synced('items').put({ id: ITEM, type: 'note', title: 'With files', access: 'edit', deleted: false })
    await db
      .synced('files')
      .bulkPut([
        row('pending', { status: 'pending', createdAt: '2026-11-20T10:00:01Z' }),
        row('failed', { status: 'pending', createdAt: '2026-11-20T10:00:02Z' }),
        row('remote', { createdAt: '2026-11-20T10:00:03Z' }),
        row('cover', { purpose: 'cover', contentType: 'image/png' }),
      ])
    await db.blobs.bulkPut([blob('pending'), blob('failed', { error: 'The file arrived damaged.' })])
  })

  afterEach(async () => {
    useDatabase(null)
    await db.delete()
  })

  function mount(canEdit = true) {
    return mountWithUi(AttachmentsPanel, {
      props: { itemId: ITEM, canEdit },
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService],
        provide: { [filePipelineKey as symbol]: files },
      },
    })
  }

  const lines = (w: ReturnType<typeof mount>) => w.findAll('li[data-testid^="attachment-"]')

  it('lists the attachments (not the cover) with their state on this device', async () => {
    const w = mount()
    await until(async () => {
      await flushPromises()
      return lines(w).length === 3 && w.text().includes('Failed')
    }, 'the attachments')
    expect(w.text()).toContain('Attachments (3)')
    expect(w.get('[data-testid="attachment-pending"]').text()).toContain('Pending upload')
    expect(w.get('[data-testid="attachment-failed"]').text()).toContain('Failed')
    expect(w.get('[data-testid="attachment-remote"]').text()).toContain('Not available offline')
    expect(w.get('[data-testid="attachment-remote"]').text()).toContain('2 KB')
    expect(w.get('[aria-label="Download remote.pdf"]').attributes('disabled')).toBeDefined()
    expect(w.get('[aria-label="Download pending.pdf"]').attributes('disabled')).toBeUndefined()

    const retry = vi.spyOn(files, 'retry').mockResolvedValue({ uploaded: 0, failed: 0, stopped: 'offline' })
    await w.get('[aria-label="Retry the upload of failed.pdf"]').trigger('click')
    expect(retry).toHaveBeenCalledWith('failed')
    w.unmount()
  })

  it('adds picked files: stored on this device, pending upload', async () => {
    const w = mount()
    await until(async () => {
      await flushPromises()
      return lines(w).length === 3
    }, 'the attachments')
    const input = w.get('[data-testid="attachments-input"]')
    const file = new File([new Uint8Array([5, 6])], 'minutes.txt', { type: 'text/plain' })
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await until(async () => {
      await flushPromises()
      return lines(w).length === 4
    }, 'the new attachment')
    expect(w.text()).toContain('minutes.txt')
    const outbox = await db.outbox.toArray()
    expect(outbox.map((e) => [e.table, e.op, e.row?.fileName])).toEqual([['files', 'insert', 'minutes.txt']])
    w.unmount()
  })

  it('is read-only without edit access', async () => {
    const w = mount(false)
    await until(async () => {
      await flushPromises()
      return lines(w).length === 3
    }, 'the attachments')
    expect(w.text()).not.toContain('Add files')
    expect(w.find('[aria-label="Delete pending.pdf"]').exists()).toBe(false)
    expect(w.find('[aria-label="Retry the upload of failed.pdf"]').exists()).toBe(false)
    w.unmount()
  })
})
