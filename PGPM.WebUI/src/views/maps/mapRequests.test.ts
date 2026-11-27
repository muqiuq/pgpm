import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PgpmDb } from '@/local/db'
import { Repo } from '@/local/repo'
import type { FileRow, Stored } from '@/local/rows'
import type { ImageRequest, ItemRequest, PlaceRequest } from '@/maps/tools'
import { memoryDb } from '@/test/memoryDb'
import { imageSize, useMapRequests } from './mapRequests'

function request<E>(extra: E) {
  return {
    at: { x: 10, y: 20 },
    place: vi.fn(),
    cancel: vi.fn(),
    onMap: new Set<string>(['already']),
    extra,
  }
}

describe('the map page’s answers to the placing tools (doc 01 §12.4)', () => {
  let db: PgpmDb
  let repo: Repo

  beforeEach(() => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => 'u-ana' })
  })
  afterEach(async () => {
    await db.delete()
  })

  function setup(files = { attachFile: vi.fn() }, picked: File | null = null) {
    const errors: string[] = []
    const requests = useMapRequests({
      mapId: () => 'map-1',
      files,
      repo,
      pickImage: async () => picked,
      onError: (_e, summary) => errors.push(summary),
    })
    return { requests, files, errors }
  }

  it('creates a new item with its shares in one batch, then places its card', async () => {
    const { requests } = setup()
    const r = request({ type: 'idea' as const }) as unknown as ItemRequest
    requests.onRequestItem(r)
    expect(requests.pending.value?.kind).toBe('item')
    const item = await requests.createAndPlace({
      title: ' Chatbot ',
      statusId: 's-new',
      shares: [{ groupId: 'g1', access: 'edit' }],
    })
    expect(r.place).toHaveBeenCalledWith(item!.id)
    expect(requests.pending.value).toBeNull()
    const outbox = await db.outbox.orderBy('seq').toArray()
    expect(outbox.map((e) => [e.table, e.op])).toEqual([
      ['items', 'insert'],
      ['item_shares', 'insert'],
    ])
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    expect(outbox[0]!.row).toMatchObject({ type: 'idea', title: 'Chatbot', statusId: 's-new' })
  })

  it('places an existing item (one already on the map too), a milestone, a project; cancels', () => {
    const { requests } = setup()
    const item = request({ type: 'note' as const }) as unknown as ItemRequest
    requests.onRequestItem(item)
    requests.place('already')
    expect(item.place).toHaveBeenCalledWith('already')
    const milestone = request({}) as unknown as PlaceRequest
    requests.onRequestMilestone(milestone)
    expect(requests.pending.value?.kind).toBe('milestone')
    requests.place('m1')
    expect(milestone.place).toHaveBeenCalledWith('m1')
    const project = request({}) as unknown as PlaceRequest
    requests.onRequestProject(project)
    requests.cancel()
    expect(project.cancel).toHaveBeenCalled()
    expect(project.place).not.toHaveBeenCalled()
    expect(requests.pending.value).toBeNull()
  })

  it('stores a pasted image as the map’s file and places it at its size', async () => {
    const row = { id: 'f1', width: 960, height: 400 } as Stored<FileRow>
    const files = { attachFile: vi.fn(async () => row) }
    const { requests } = setup(files)
    const file = new File([new Uint8Array([1])], 'shot.png', { type: 'image/png' })
    const r = request({ file }) as unknown as ImageRequest
    await requests.onRequestImage(r)
    expect(files.attachFile).toHaveBeenCalledWith({ mapId: 'map-1' }, file, 'map_image')
    expect(r.place).toHaveBeenCalledWith('f1', { width: 480, height: 200 })
  })

  it('asks for an image when none was pasted; nothing picked or a refusal cancels', async () => {
    const files = { attachFile: vi.fn(async () => Promise.reject(new Error('too large'))) }
    const picked = new File([new Uint8Array([1])], 'a.png', { type: 'image/png' })
    const { requests, errors } = setup(files, picked)
    const refused = request({ file: null }) as unknown as ImageRequest
    await requests.onRequestImage(refused)
    expect(files.attachFile).toHaveBeenCalledWith({ mapId: 'map-1' }, picked, 'map_image')
    expect(refused.cancel).toHaveBeenCalled()
    expect(errors).toEqual(['Image not added'])
    const none = setup({ attachFile: vi.fn() }, null)
    const nothing = request({ file: null }) as unknown as ImageRequest
    await none.requests.onRequestImage(nothing)
    expect(nothing.cancel).toHaveBeenCalled()
    expect(none.files.attachFile).not.toHaveBeenCalled()
  })

  it('sizes images within 480 × 360, the aspect kept', () => {
    expect(imageSize(100, 50)).toEqual({ width: 100, height: 50 })
    expect(imageSize(4000, 3000)).toEqual({ width: 480, height: 360 })
    expect(imageSize(300, 3000)).toEqual({ width: 36, height: 360 })
    expect(imageSize(null, null)).toEqual({ width: 240, height: 160 })
  })
})
