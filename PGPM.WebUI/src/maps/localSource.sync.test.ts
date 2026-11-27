import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { createMap, type MapRecord } from '@/local/maps'
import type { ItemRow } from '@/local/rows'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import { createLocalSource, type LocalMapSource } from './localSource'

/**
 * Maps between devices (doc 01 §12.10, docs/05 phase 5 *Done when*): the local data source of two users' devices
 * against the fake server – live co-editing, offline editing with a conflict per map, and a map that is no longer
 * shared leaving the device. The browser tests (e2e/maps.spec.ts) do the same against the real server.
 */
const ANA = '01970000-0000-7000-8000-00000000a001'
const BEN = '01970000-0000-7000-8000-00000000b001'
const G1 = '01970000-0000-7000-8000-000000000001'

describe('a map between two devices', () => {
  let server: FakeSyncServer
  let clients: TestClient[]
  let sources: LocalMapSource[]

  beforeEach(() => {
    server = new FakeSyncServer()
    server.addUser({ id: ANA, name: 'Ana Muster', groupIds: [G1] })
    server.addUser({ id: BEN, name: 'Ben Beispiel', groupIds: [G1] })
    server.addGroup(G1, 'Innovation team')
    clients = []
    sources = []
  })
  afterEach(async () => {
    for (const s of sources) s.dispose()
    for (const c of clients) await c.engine.stop()
    for (const c of clients) await c.db.delete()
  })

  function client(userId: string) {
    const c = testClient(server, userId)
    clients.push(c)
    c.engine.start()
    return c
  }
  function sourceOf(c: TestClient, mapId: string) {
    const s = createLocalSource(mapId, { db: () => c.db, repo: c.repo })
    sources.push(s)
    return s
  }
  const live = (...cs: TestClient[]) =>
    until(() => cs.every((c) => c.engine.status.connection === 'live'), 'the live channel')

  /** Ana's map, shared with G1 at edit, with a rectangle and a card of her idea; both devices have it. */
  async function sharedMap(ana: TestClient, ben: TestClient) {
    const map = await ana.repo.transaction(async (tx) => {
      const idea = await tx.create<ItemRow>('items', { type: 'idea', title: 'Chatbot', statusId: 's1' })
      await tx.create('item_shares', { itemId: idea.id, groupId: G1, access: 'read' })
      return createMap(tx, { kind: 'canvas', name: 'Workshop', shares: [{ groupId: G1, access: 'edit' }] })
    })
    const a = sourceOf(ana, map.id)
    const b = sourceOf(ben, map.id)
    await until(() => a.layers.value.length === 1, 'the layer at Ana')
    const rect = a.newId()
    await a.create('map_elements', {
      id: rect,
      map_id: map.id,
      layer_id: a.layers.value[0]!.id,
      kind: 'rect',
      z: 'a0',
      x: 0,
      y: 0,
      width: 100,
      height: 60,
      style: {},
    })
    await until(() => b.elements.value.length === 1 && b.access.value === 'edit', 'the map at Ben')
    return { map: map as MapRecord, a, b, rect }
  }

  it('spreads a move to the co-editor live, within a second', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    await live(ana, ben)
    const { a, b, rect } = await sharedMap(ana, ben)
    const started = Date.now()
    await b.batch(() => {
      void b.patch('map_elements', rect, { x: { old: 0, new: 240 }, y: { old: 0, new: 80 } })
    })
    await until(() => a.elements.value.find((e) => e.id === rect)?.x === 240, 'Ben’s move at Ana')
    expect(Date.now() - started).toBeLessThan(1000)
    // Two people changing different elements don't collide.
    const other = a.elements.value.find((e) => e.id === rect)!
    await a.patch('map_elements', rect, { 'style.fill': { old: undefined, new: '#fde68a' } })
    await until(() => b.elements.value.find((e) => e.id === rect)?.style?.fill === '#fde68a', 'Ana’s fill at Ben')
    expect(other.y).toBe(80)
    expect(await ana.db.conflicts.count()).toBe(0)
  })

  it('keeps offline changes and turns a collision into a conflict of that map', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    await live(ana, ben)
    const { map, a, b, rect } = await sharedMap(ana, ben)
    ben.goOffline()
    await until(() => ben.engine.status.connection !== 'live', 'Ben offline')
    // Offline, Ben moves the rectangle and adds a text; the map works as before.
    await b.patch('map_elements', rect, { x: { old: 0, new: 50 } })
    const text = b.newId()
    await b.create('map_elements', {
      id: text,
      map_id: map.id,
      layer_id: b.layers.value[0]!.id,
      kind: 'text',
      z: 'a1',
      x: 0,
      y: 200,
      width: 200,
      height: 40,
      text: 'Offline note',
      style: {},
    })
    await until(() => b.elements.value.length === 2 && b.elements.value.find((e) => e.id === rect)?.x === 50, 'offline')
    // Meanwhile Ana moves it elsewhere.
    await a.patch('map_elements', rect, { x: { old: 0, new: 300 } })
    await until(() => server.table('map_elements').get(rect)?.x === 300, 'Ana’s move on the server')
    ben.goOnline()
    await until(async () => (await ben.db.conflicts.count()) === 1, 'the conflict at Ben')
    const [conflict] = await ben.db.conflicts.toArray()
    expect(conflict).toMatchObject({ table: 'map_elements', rowId: rect, mapId: map.id, kind: 'field' })
    expect(conflict!.fields.x).toMatchObject({ mine: 50, theirs: 300 })
    // The text arrived; the rectangle shows theirs until Ben decides.
    await until(() => a.elements.value.some((e) => e.id === text), 'Ben’s text at Ana')
    await until(() => b.elements.value.find((e) => e.id === rect)?.x === 300, 'theirs at Ben')
  })

  it('leaves the device of a user the map is no longer shared with', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    await live(ana, ben)
    const { map, b } = await sharedMap(ana, ben)
    server.putMapShares(ANA, map.id, [])
    await until(() => b.map.value === null, 'the map gone at Ben')
    expect(b.access.value).toBe('read')
    expect(await ben.db.synced('map_elements').where('mapId').equals(map.id).count()).toBe(0)
    expect(await ben.db.synced('maps').get(map.id)).toBeUndefined()
  })
})
