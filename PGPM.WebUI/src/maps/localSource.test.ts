import { afterEach, describe, expect, it } from 'vitest'
import type { PgpmDb } from '@/local/db'
import { toBase64 } from '@/local/images'
import { Repo } from '@/local/repo'
import { storeMapSeed } from '@/test/mapSeed'
import { memoryDb, until } from '@/test/memoryDb'
import { describeDataSource } from './dataSourceBehaviour'
import { createLocalSource, type LocalMapSource } from './localSource'
import type { MemorySeed } from './memorySource'
import { C1, IDEA, MAP_ID, NOTE, PROJECT, R1, seed, uid } from './testFixtures'

const opened: { db: PgpmDb; source: LocalMapSource }[] = []
afterEach(async () => {
  for (const { db, source } of opened.splice(0)) {
    source.dispose()
    await db.delete()
  }
})

const fakeUrls = () => {
  const live = new Set<string>()
  let n = 0
  return {
    live,
    create: () => {
      const url = `blob:test/${++n}`
      live.add(url)
      return url
    },
    revoke: (url: string) => live.delete(url),
  }
}

async function open(data: MemorySeed = seed(), extra: Parameters<typeof createLocalSource>[1] = {}) {
  const db = memoryDb()
  await storeMapSeed(db, data)
  const repo = new Repo({ db: () => db, userId: () => 'u-ana' })
  const source = createLocalSource(data.map.id, { db: () => db, repo, ...extra })
  opened.push({ db, source })
  await until(() => source.loaded.value, 'the map')
  return { db, repo, source }
}

describeDataSource('the local database’s data source', async (data) => {
  const { db, source } = await open(data)
  return {
    source,
    written: async () =>
      (await db.outbox.orderBy('seq').toArray())
        .filter((e) => e.table.startsWith('map'))
        .map((e) => ({ batch: e.batch, table: e.table, op: e.op, id: e.id })),
    setAccess: async (access) => {
      const row = (await db.synced('maps').get(MAP_ID))!
      await db.synced('maps').put({ ...row, access, seq: (row.seq as number) + 100 })
    },
  }
})

describe('the local database’s data source', () => {
  it('reads the map’s rows in the map UI’s columns, and what its elements refer to', async () => {
    const MILESTONE = uid('m1')
    const { source } = await open(
      seed({
        milestones: [
          { id: MILESTONE, item_id: PROJECT, kind: 'gate', title: 'G1', status: 'planned', sort_order: 'a0' },
          {
            id: uid('m2'),
            item_id: uid('999'),
            kind: 'milestone',
            title: 'Elsewhere',
            status: 'planned',
            sort_order: 'a0',
          },
        ],
        dodItems: [{ id: uid('d1'), milestone_id: MILESTONE, text: 'Done?', state: 'open', sort_order: 'a0' }],
        links: [
          { id: uid('k1'), from_item_id: IDEA, to_item_id: PROJECT, relation: 'related' },
          { id: uid('k2'), from_item_id: IDEA, to_item_id: NOTE, relation: 'related' },
        ],
        assignments: [{ id: uid('a1'), item_id: IDEA, person_id: uid('p1'), role: 'Owner' }],
        persons: [
          { id: uid('p1'), display_name: 'Petra' },
          { id: uid('p2'), display_name: 'Nobody here' },
        ],
      }),
    )
    await until(() => source.items.value.length === 2, 'the items')
    expect(source.map.value).toMatchObject({ id: MAP_ID, kind: 'canvas', settings: { gridSize: 20 }, access: 'manage' })
    expect(source.layers.value.map((l) => l.name).sort()).toEqual(['Base', 'Top'])
    const line = source.elements.value.find((e) => e.kind === 'line')!
    expect(line).toMatchObject({ start_element_id: C1, end_element_id: R1, style: { arrowEnd: 'arrow' } })
    expect(line.points).toEqual([
      [0, 0],
      [0, 260],
    ])
    // The cards' items only: the note isn't on the map.
    expect(source.items.value.map((i) => i.id).sort()).toEqual([IDEA, PROJECT].sort())
    expect(source.items.value.find((i) => i.id === IDEA)).toMatchObject({ status_id: expect.any(String) })
    expect(source.milestones.value.map((m) => m.title)).toEqual(['G1'])
    expect(source.dodItems.value.map((d) => d.milestone_id)).toEqual([MILESTONE])
    expect(source.links.value.map((l) => l.id)).toEqual([uid('k1')])
    expect(source.assignments.value.map((a) => a.person_id)).toEqual([uid('p1')])
    expect(source.persons.value.map((p) => p.display_name)).toEqual(['Petra'])
    expect(source.fieldValues.value).toHaveLength(3)
    expect(source.fieldDefinitions.value.map((f) => f.key)).toContain('feasibility')
    expect(source.statuses.value).toHaveLength(2)
  })

  it('follows the elements: a card of another item brings that item', async () => {
    const { source } = await open()
    await until(() => source.items.value.length === 2, 'the items')
    await source.create('map_elements', {
      id: source.newId(),
      map_id: MAP_ID,
      layer_id: uid('1a1'),
      kind: 'item',
      item_id: NOTE,
      z: 'a9',
      x: 0,
      y: 0,
      width: 220,
      height: 92,
    })
    await until(() => source.items.value.length === 3, 'the note')
  })

  it('reads a matrix map’s candidates by its filter, the groups through the shares', async () => {
    const G = uid('9a1')
    const data = seed({
      map: {
        id: MAP_ID,
        kind: 'matrix',
        name: 'Matrix',
        access: 'edit',
        matrix: {
          x: { field: 'feasibility', min: 0, max: 10 },
          y: { field: 'impact', min: 0, max: 100 },
          filter: { types: ['idea', 'project'], groupIds: [G] },
        },
      },
      elements: [],
    })
    const { db, source } = await open(data)
    await db.synced('item_shares').bulkPut([
      { id: uid('s1'), itemId: IDEA, groupId: G, access: 'read', seq: 90, rev: 1, deleted: false },
      { id: uid('s2'), itemId: NOTE, groupId: G, access: 'read', seq: 91, rev: 1, deleted: false },
    ])
    await until(() => source.items.value.length === 1, 'the shared idea')
    expect(source.items.value[0]!.id).toBe(IDEA)
    expect(source.fieldValues.value.map((v) => v.item_id)).toEqual([IDEA, IDEA, IDEA])
    // A share for the project too: it joins the candidates.
    await db
      .synced('item_shares')
      .put({ id: uid('s3'), itemId: PROJECT, groupId: G, access: 'read', seq: 92, rev: 1, deleted: false })
    await until(() => source.items.value.length === 2, 'the shared project')
  })

  it('keeps row objects by id, seq and rev; a pending local change or a re-stamp gives a new one', async () => {
    const { db, repo, source } = await open()
    await until(() => source.items.value.length === 2, 'the items')
    const idea = source.items.value.find((i) => i.id === IDEA)!
    const project = source.items.value.find((i) => i.id === PROJECT)!
    const items = source.items.value
    await repo.patch('items', IDEA, { title: 'Chatbot 2' })
    await until(() => source.items.value.find((i) => i.id === IDEA)?.title === 'Chatbot 2', 'the local change')
    expect(source.items.value).not.toBe(items)
    expect(source.items.value.find((i) => i.id === PROJECT)).toBe(project)
    expect(source.items.value.find((i) => i.id === IDEA)).not.toBe(idea)
    // A re-stamp (another seq, the same rev): the computed access may have changed.
    const stored = (await db.synced('items').get(PROJECT))!
    await db.synced('items').put({ ...stored, seq: 500, access: 'read' })
    await until(() => source.items.value.find((i) => i.id === PROJECT)?.access === 'read', 'the re-stamp')
    // An unrelated write somewhere else answers the same arrays.
    const elements = source.elements.value
    const layers = source.layers.value
    await repo.create('items', { type: 'note', title: 'Elsewhere' })
    await new Promise((r) => setTimeout(r, 40))
    expect(source.elements.value).toBe(elements)
    expect(source.layers.value).toBe(layers)
  })

  it('writes a batch as one transaction and one outbox batch, the old values as the user saw them', async () => {
    const { db, source } = await open()
    await until(() => source.elements.value.length > 0, 'the elements')
    const id = source.newId()
    await source.batch(() => {
      void source.create('map_elements', {
        id,
        map_id: MAP_ID,
        layer_id: uid('1a1'),
        kind: 'rect',
        z: 'a9',
        x: 10,
        y: 10,
        width: 100,
        height: 50,
        style: {},
      })
      void source.patch('map_elements', id, { 'style.fill': { old: undefined, new: '#fde68a' } })
      void source.patch('map_elements', R1, { x: { old: 7, new: 8 } })
    })
    const entries = await db.outbox.orderBy('seq').toArray()
    expect(entries.map((e) => [e.op, e.id])).toEqual([
      ['insert', id],
      ['patch', R1],
    ])
    expect(entries[0]!.row).toMatchObject({ mapId: MAP_ID, layerId: uid('1a1'), style: { fill: '#fde68a' } })
    // The value the user saw (7) is what the server compares with, not the local one (0).
    expect(entries[1]!.changes).toEqual({ x: { old: 7, new: 8 } })
    expect(new Set(entries.map((e) => e.batch)).size).toBe(1)
  })

  it('fails one write of a batch alone (a row gone), and stores the others', async () => {
    const { source } = await open()
    await until(() => source.elements.value.length > 0, 'the elements')
    const results: string[] = []
    await source.batch(() => {
      source.patch('map_elements', 'gone', { x: { old: 0, new: 1 } }).catch(() => results.push('gone refused'))
      void source.patch('map_elements', R1, { x: { old: 0, new: 9 } }).then(() => results.push('R1 stored'))
    })
    await until(() => results.length === 2, 'both answers')
    expect(results.sort()).toEqual(['R1 stored', 'gone refused'])
    await until(() => source.elements.value.find((e) => e.id === R1)?.x === 9, 'the patch')
  })

  it('keeps the personal view in user_settings map:<id>, one row, patched afterwards', async () => {
    const { db, source } = await open()
    await source.setViewSettings({ showLinks: true, types: ['idea'] })
    await source.setViewSettings({ showLinks: false, types: ['idea'], viewport: { x: 0, y: 0, k: 2 } })
    const rows = await db.synced('user_settings').where('key').equals(`map:${MAP_ID}`).toArray()
    expect(rows).toHaveLength(1)
    expect(rows[0]!.value).toEqual({ showLinks: false, types: ['idea'], viewport: { x: 0, y: 0, k: 2 } })
    const outbox = await db.outbox.toArray()
    expect(outbox.map((e) => e.op)).toEqual(['insert'])
    // Another device's setting arrives through the sync.
    await db
      .synced('user_settings')
      .put({ ...rows[0]!, value: { showLinks: true }, seq: 900, rev: 3, _local: undefined })
    await until(() => source.viewSettings.value?.showLinks === true, 'the synced setting')
  })

  it('answers blob URLs from the device – the thumbnail first –, waits for bytes, and revokes them when disposed', async () => {
    const urls = fakeUrls()
    const { db, source } = await open(seed(), { objectUrls: urls })
    const file = (id: string, thumbnail: string | null) => ({
      id,
      mapId: MAP_ID,
      itemId: null,
      purpose: 'map_image',
      fileName: 'a.png',
      contentType: 'image/png',
      sizeBytes: 3,
      sha256: 'x',
      thumbnail,
      status: 'available',
      seq: 700,
      rev: 1,
      deleted: false,
    })
    await db.synced('files').bulkPut([file('f1', toBase64(new Uint8Array([1, 2, 3]))), file('f2', null)])
    const blob = {
      bytes: new Uint8Array([1, 2, 3]).buffer,
      type: 'image/png',
      size: 3,
      cachedAt: '',
      origin: 'download',
    }
    await db.blobs.put({ id: 'f1', ...blob, uploaded: true } as never)
    const thumb = await source.fileUrl('f1', { thumbnail: true })
    const full = await source.fileUrl('f1')
    expect(thumb).toMatch(/^blob:/)
    expect(full).toMatch(/^blob:/)
    expect(full).not.toBe(thumb)
    expect(await source.fileUrl('f1', { thumbnail: true })).toBe(thumb)
    // Not on the device yet: answered once the bytes arrive (the prefetch).
    const later = source.fileUrl('f2', { thumbnail: true })
    await new Promise((r) => setTimeout(r, 20))
    await db.blobs.put({ id: 'f2', ...blob, uploaded: true } as never)
    expect(await later).toMatch(/^blob:/)
    expect(urls.live.size).toBe(3)
    // Waiting for a file that never comes ends with the source.
    const never = source.fileUrl('f3')
    source.dispose()
    expect(await never).toBeNull()
    expect(urls.live.size).toBe(0)
    expect(await source.fileUrl('f1')).toBeNull()
  })

  it('downloads bytes that aren’t on the device through the file pipeline', async () => {
    const urls = fakeUrls()
    const asked: string[] = []
    const { db, source } = await open(seed(), {
      objectUrls: urls,
      files: {
        fileBlob: async (id) => {
          asked.push(id)
          return new Blob([new Uint8Array([9])], { type: 'image/png' })
        },
      },
    })
    await db.synced('files').put({ id: 'f9', mapId: MAP_ID, purpose: 'map_image', thumbnail: null, seq: 9, rev: 1 })
    expect(await source.fileUrl('f9')).toMatch(/^blob:/)
    expect(asked).toEqual(['f9'])
  })
})
