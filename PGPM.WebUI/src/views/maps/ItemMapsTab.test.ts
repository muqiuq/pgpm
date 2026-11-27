import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase, type PgpmDb } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import type { ItemRow, Stored } from '@/local/rows'
import { IDEA, MAP_ID, seed } from '@/maps/testFixtures'
import { storeMapSeed } from '@/test/mapSeed'
import { memoryDb, until } from '@/test/memoryDb'
import { button, mountPage } from '@/test/mountPage'
import ItemMapsTab from './ItemMapsTab.vue'

const synced = { seq: 1, rev: 1, deleted: false, _local: { serverRev: 1, pending: false } }

describe('an item’s Maps tab (doc 01 §4.4, §12.4)', () => {
  let db: PgpmDb
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    // The fixtures' canvas (two cards of the idea), a matrix map that shows ideas, an empty canvas, a template.
    await storeMapSeed(db, seed())
    await db.synced('maps').bulkPut([
      {
        ...synced,
        id: 'mx',
        kind: 'matrix',
        name: 'Review board',
        access: 'read',
        matrix: {
          x: { field: 'feasibility', min: 0, max: 10 },
          y: { field: 'impact', min: 0, max: 100 },
          quadrants: { tr: 'Stars' },
          filter: { types: ['idea'] },
        },
      },
      {
        ...synced,
        id: 'leads',
        kind: 'matrix',
        name: 'Leads',
        access: 'edit',
        matrix: { filter: { types: ['lead'] } },
      },
      { ...synced, id: 'empty', kind: 'canvas', name: 'Empty board', access: 'edit', isTemplate: false },
      { ...synced, id: 'tpl', kind: 'canvas', name: 'Crazy 8s', access: 'read', isTemplate: true },
    ])
    await db.synced('map_layers').put({
      ...synced,
      id: 'EL',
      mapId: 'empty',
      name: 'Base',
      sortOrder: 'a0',
      isVisible: true,
      isLocked: false,
    })
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  it('lists the maps the item is on – how often, its quadrant – and opens them centred on it', async () => {
    const item = (await db.synced('items').get(IDEA)) as unknown as Stored<ItemRow>
    const wrapper = mountPage(ItemMapsTab, { item })
    mounted.push(wrapper)
    await until(() => wrapper.text().includes('Review board'), 'the matrix map')
    await until(() => wrapper.text().includes('Test map'), 'the canvas')
    const rows = [...document.querySelectorAll('[data-map]')].map((r) => r.textContent!)
    expect(rows).toHaveLength(2)
    expect(rows[0]).toMatch(/Review board.*Matrix.*Stars/)
    expect(rows[1]).toMatch(/Test map.*Canvas.*Placed 2×/)
    const link = document.querySelector(`[data-map="${MAP_ID}"] a`)!.getAttribute('data-to')!
    expect(JSON.parse(link)).toEqual({ name: 'map', params: { id: MAP_ID }, query: { focus: `item:${IDEA}` } })
  })

  it('adds a card to a canvas map the user may edit, in its middle', async () => {
    const item = (await db.synced('items').get(IDEA)) as unknown as Stored<ItemRow>
    const wrapper = mountPage(ItemMapsTab, { item })
    mounted.push(wrapper)
    await until(() => !!button('Add to map…'), 'the button')
    button('Add to map…')!.click()
    await until(() => !!button(/Empty board/), 'the map picker')
    // Matrix maps and templates aren't offered; the canvas with the idea shows how often.
    expect(button(/Review board/)).toBeUndefined()
    expect(button(/Crazy 8s/)).toBeUndefined()
    await until(() => !!button(/Test map/)?.textContent?.includes('2×'), 'the count')
    button(/Empty board/)!.click()
    await until(async () => (await db.synced('map_elements').where('mapId').equals('empty').count()) === 1, 'the card')
    const [card] = await db.synced('map_elements').where('mapId').equals('empty').toArray()
    expect(card).toMatchObject({ kind: 'item', itemId: IDEA, layerId: 'EL', x: -110, y: -46 })
    await until(() => wrapper.text().includes('Empty board'), 'the new row')
  })
})
