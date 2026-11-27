import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { PgpmDb } from './db'
import {
  addCardsToMap,
  addCardToMap,
  cardGrid,
  createMap,
  duplicateMap,
  mapCentre,
  mapContent,
  placementsOf,
  targetLayer,
} from './maps'
import { Repo } from './repo'

const synced = { seq: 1, rev: 1, deleted: false, _local: { serverRev: 1, pending: false } }

describe('maps on the device (doc 01 §12.1, §12.9)', () => {
  let db: PgpmDb
  let repo: Repo

  beforeEach(async () => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => 'u-ana' })
    await db.synced('items').bulkPut([
      { ...synced, id: 'i1', type: 'idea', title: 'Chatbot' },
      { ...synced, id: 'p1', type: 'project', title: 'Website' },
    ])
    await db.synced('milestones').put({ ...synced, id: 'm1', itemId: 'p1', title: 'G1' })
    // A template: two layers, a frame, a sticky text, a connected line, a card and an image.
    await db.synced('maps').put({
      ...synced,
      id: 'tpl',
      kind: 'canvas',
      name: 'Impact field canvas',
      description: '2 × 2',
      ownerUserId: 'u-admin',
      settings: { grid: false, background: '#ffffff' },
      matrix: null,
      defaultView: null,
      isTemplate: true,
      templateSourceId: null,
      preview: 'UklGRgAAAA',
      access: 'read',
    })
    await db.synced('map_layers').bulkPut([
      { ...synced, id: 'L1', mapId: 'tpl', name: 'Frame', sortOrder: 'a0', isVisible: true, isLocked: true },
      { ...synced, id: 'L2', mapId: 'tpl', name: 'Notes', sortOrder: 'a1', isVisible: true, isLocked: false },
    ])
    const el = (id: string, extra: Record<string, unknown>) => ({
      ...synced,
      id,
      mapId: 'tpl',
      layerId: 'L1',
      z: 'a0',
      x: 0,
      y: 0,
      width: 100,
      height: 50,
      style: {},
      ...extra,
    })
    await db.synced('map_elements').bulkPut([
      el('rect', { kind: 'rect', text: 'Impact' }),
      el('text', { kind: 'text', layerId: 'L2', text: 'Hello', x: 400, y: 200 }),
      el('line', {
        kind: 'line',
        points: [
          [0, 0],
          [10, 10],
        ],
        startElementId: 'rect',
        endElementId: 'card',
        z: 'a1',
      }),
      el('card', { kind: 'item', itemId: 'i1', layerId: 'L2', z: 'a1' }),
      el('ghost', { kind: 'item', itemId: 'not-here', layerId: 'L2', z: 'a2' }),
      el('ms', { kind: 'milestone', milestoneId: 'm1', layerId: 'L2', z: 'a3' }),
      el('img', { kind: 'image', fileId: 'f1', layerId: 'L2', z: 'a4' }),
    ])
    await db.synced('files').put({
      ...synced,
      id: 'f1',
      mapId: 'tpl',
      itemId: null,
      purpose: 'map_image',
      fileName: 'a.png',
      contentType: 'image/png',
      sizeBytes: 3,
      sha256: 'abc',
      width: 30,
      height: 20,
      thumbnail: 'xyz',
      status: 'available',
    })
    await db.blobs.put({
      id: 'f1',
      bytes: new Uint8Array([1, 2, 3]).buffer,
      type: 'image/png',
      size: 3,
      cachedAt: '2026-01-01T00:00:00Z',
      origin: 'download',
      uploaded: true,
    })
  })
  afterEach(async () => {
    await db.delete()
  })

  const rowsOf = async (table: string, mapId: string) =>
    (await db.synced(table).where('mapId').equals(mapId).toArray()).filter((r) => !r.deleted)

  it('creates a canvas with its layer Base and its shares in one batch', async () => {
    const map = await repo.transaction((tx) =>
      createMap(tx, { kind: 'canvas', name: '  Workshop ', shares: [{ groupId: 'g1', access: 'edit' }] }),
    )
    expect(map).toMatchObject({
      kind: 'canvas',
      name: 'Workshop',
      ownerUserId: 'u-ana',
      isTemplate: false,
      matrix: null,
    })
    expect(map.settings).toEqual({ grid: true, snap: false, gridSize: 20 })
    expect((await rowsOf('map_layers', map.id)).map((l) => l.name)).toEqual(['Base'])
    expect((await rowsOf('map_shares', map.id)).map((s) => [s.groupId, s.access])).toEqual([['g1', 'edit']])
    const outbox = await db.outbox.orderBy('seq').toArray()
    expect(outbox.map((e) => e.table)).toEqual(['maps', 'map_layers', 'map_shares'])
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    // The owner and the access are the server's.
    expect(outbox[0]!.row).not.toHaveProperty('access')
  })

  it('makes a map from a template: its layers and drawings with new ids, not its cards; images get files of their own', async () => {
    const template = (await mapContent(db, 'tpl'))!
    const map = await repo.transaction((tx) =>
      createMap(tx, { kind: 'canvas', name: 'Our impact field', shares: [], template }),
    )
    expect(map).toMatchObject({
      name: 'Our impact field',
      templateSourceId: 'tpl',
      isTemplate: false,
      description: '2 × 2',
    })
    expect(map.preview ?? null).toBeNull()
    const layers = await rowsOf('map_layers', map.id)
    expect(layers.map((l) => [l.name, l.sortOrder, l.isLocked]).sort()).toEqual([
      ['Frame', 'a0', true],
      ['Notes', 'a1', false],
    ])
    const elements = await rowsOf('map_elements', map.id)
    expect(elements.map((e) => e.kind).sort()).toEqual(['image', 'line', 'rect', 'text'])
    const rect = elements.find((e) => e.kind === 'rect')!
    const line = elements.find((e) => e.kind === 'line')!
    expect(rect.id).not.toBe('rect')
    expect(line.startElementId).toBe(rect.id)
    expect(line.endElementId).toBeNull()
    const image = elements.find((e) => e.kind === 'image')!
    expect(image.fileId).not.toBe('f1')
    const file = (await db.synced('files').get(image.fileId as string))!
    expect(file).toMatchObject({ mapId: map.id, purpose: 'map_image', sha256: 'abc', thumbnail: 'xyz' })
    expect(await db.blobs.get(image.fileId as string)).toMatchObject({ uploaded: true, size: 3 })
    // Rows before what refers to them: the file before its image, the rect before the line.
    const order = (await db.outbox.orderBy('seq').toArray()).map((e) => e.id)
    expect(order.indexOf(file.id)).toBeLessThan(order.indexOf(image.id))
    expect(order.indexOf(rect.id)).toBeLessThan(order.indexOf(line.id))
  })

  it('takes a matrix preset’s axes and layer', async () => {
    await db.synced('maps').put({
      ...synced,
      id: 'preset',
      kind: 'matrix',
      name: 'Prioritization matrix',
      ownerUserId: 'u-admin',
      settings: { cardBadges: ['impact'] },
      matrix: { x: { field: 'feas', min: 0, max: 65 }, y: { field: 'impact', min: 0, max: 100 } },
      isTemplate: true,
      access: 'read',
    })
    await db.synced('map_layers').put({
      ...synced,
      id: 'PL',
      mapId: 'preset',
      name: 'Base',
      sortOrder: 'a0',
      isVisible: true,
      isLocked: false,
    })
    const template = (await mapContent(db, 'preset'))!
    const map = await repo.transaction((tx) =>
      createMap(tx, { kind: 'matrix', name: 'Review board', shares: [], template }),
    )
    expect(map).toMatchObject({ kind: 'matrix', settings: { cardBadges: ['impact'] }, templateSourceId: 'preset' })
    expect(map.matrix).toEqual({ x: { field: 'feas', min: 0, max: 65 }, y: { field: 'impact', min: 0, max: 100 } })
    expect((await rowsOf('map_layers', map.id)).map((l) => l.name)).toEqual(['Base'])
  })

  it('duplicates a map with everything on it the user can see; cards of items not on the device stay behind', async () => {
    const content = (await mapContent(db, 'tpl'))!
    const map = await repo.transaction((tx) =>
      duplicateMap(tx, content, { name: 'Copy of Impact field canvas', shares: [{ groupId: 'g1', access: 'read' }] }),
    )
    expect(map).toMatchObject({ name: 'Copy of Impact field canvas', isTemplate: false, ownerUserId: 'u-ana' })
    expect(map.settings).toEqual({ grid: false, background: '#ffffff' })
    const elements = await rowsOf('map_elements', map.id)
    expect(elements.map((e) => e.kind).sort()).toEqual(['image', 'item', 'line', 'milestone', 'rect', 'text'])
    const card = elements.find((e) => e.kind === 'item')!
    const line = elements.find((e) => e.kind === 'line')!
    expect(card.itemId).toBe('i1')
    expect(line.endElementId).toBe(card.id)
    expect((await rowsOf('map_shares', map.id)).map((s) => s.access)).toEqual(['read'])
  })

  it('puts a card at a map’s centre, on its topmost free layer, on top', async () => {
    const map = await repo.transaction((tx) => createMap(tx, { kind: 'canvas', name: 'Board', shares: [] }))
    const layer = (await rowsOf('map_layers', map.id))[0]!
    await db.synced('map_elements').put({
      ...synced,
      id: 'box',
      mapId: map.id,
      layerId: layer.id,
      kind: 'rect',
      z: 'a5',
      x: 0,
      y: 0,
      width: 1000,
      height: 600,
    })
    const id = await repo.transaction((tx) => addCardToMap(tx, map.id, 'p1'))
    const card = (await db.synced('map_elements').get(id))!
    expect(card).toMatchObject({
      kind: 'item',
      itemId: 'p1',
      layerId: layer.id,
      x: 390,
      y: 254,
      config: { size: 'compact' },
    })
    expect((card.z as string) > 'a5').toBe(true)
    expect(await placementsOf(db, 'p1')).toEqual(new Map([[map.id, 1]]))
    expect(await placementsOf(db, 'i1')).toEqual(new Map([['tpl', 1]]))
  })

  it('adds several cards in one batch, in a grid around the centre, each on top of the one before', async () => {
    const map = await repo.transaction((tx) => createMap(tx, { kind: 'canvas', name: 'Board', shares: [] }))
    await db.outbox.clear()
    const ids = await repo.transaction((tx) => addCardsToMap(tx, map.id, ['i1', 'i2', 'i3', 'i4', 'i5']))
    const cards = await Promise.all(ids.map(async (id) => (await db.synced('map_elements').get(id))!))
    expect(cards.map((c) => c.itemId)).toEqual(['i1', 'i2', 'i3', 'i4', 'i5'])
    // Three columns, two rows around the empty map's origin: 3 × 220 + 2 × 24 wide, 2 × 92 + 24 high.
    expect(cards.map((c) => [c.x, c.y])).toEqual([
      [-354, -104],
      [-110, -104],
      [134, -104],
      [-354, 12],
      [-110, 12],
    ])
    const zs = cards.map((c) => c.z as string)
    expect([...zs].sort()).toEqual(zs)
    expect(new Set((await db.outbox.toArray()).map((e) => e.batch)).size).toBe(1)
    expect(cardGrid({ x: 0, y: 0 }, 1)).toEqual([{ x: -110, y: -46 }])
  })

  it('refuses cards on a map one may only read', async () => {
    await db.synced('maps').put({ ...synced, id: 'ro', kind: 'canvas', name: 'R', access: 'read' })
    await db.synced('map_layers').put({
      ...synced,
      id: 'ro-base',
      mapId: 'ro',
      name: 'Base',
      sortOrder: 'a0',
      isVisible: true,
      isLocked: false,
    })
    await expect(repo.transaction((tx) => addCardsToMap(tx, 'ro', ['i1']))).rejects.toThrow(/read this map/)
  })

  it('refuses a card on a matrix map, and finds no layer when all are locked', async () => {
    await db.synced('maps').put({ ...synced, id: 'mx', kind: 'matrix', name: 'M', matrix: {}, access: 'edit' })
    await expect(repo.transaction((tx) => addCardToMap(tx, 'mx', 'i1'))).rejects.toThrow(/matrix/)
    expect(
      targetLayer([{ id: 'a', map_id: 'm', name: 'A', sort_order: 'a0', is_visible: true, is_locked: true }]),
    ).toBeNull()
    expect(mapCentre([])).toEqual({ x: 0, y: 0 })
  })
})
