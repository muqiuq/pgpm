import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase, type PgpmDb } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import { memoryDb, until } from '@/test/memoryDb'
import { button, mountPage } from '@/test/mountPage'
import BulkActions from './BulkActions.vue'

const synced = { seq: 1, rev: 1, deleted: false, _local: { serverRev: 1, pending: false } }

describe('bulk Add to map (docs/01 §4.3)', () => {
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
    await db.synced('maps').bulkPut([
      { ...synced, id: 'board', kind: 'canvas', name: 'Workshop board', access: 'edit', isTemplate: false },
      { ...synced, id: 'seen', kind: 'canvas', name: 'Read-only board', access: 'read', isTemplate: false },
      { ...synced, id: 'mx', kind: 'matrix', name: 'Matrix', access: 'manage', matrix: {} },
    ])
    await db.synced('map_layers').put({
      ...synced,
      id: 'base',
      mapId: 'board',
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

  it('puts a card of every picked item on a canvas map one may edit, in one batch', async () => {
    const wrapper = mountPage(BulkActions, { type: 'idea', ids: ['i1', 'i2', 'i3'] })
    mounted.push(wrapper)
    button('Add to map…')!.click()
    await until(() => !!button(/Workshop board/), 'the map picker')
    // Only canvases one may edit.
    expect(button(/Read-only board/)).toBeUndefined()
    expect(button(/Matrix/)).toBeUndefined()
    expect(document.body.textContent).toContain('Add 3 ideas to a map')
    button(/Workshop board/)!.click()
    await until(async () => (await db.synced('map_elements').count()) === 3, 'the cards')
    const cards = await db.synced('map_elements').toArray()
    expect(cards.map((c) => c.itemId).sort()).toEqual(['i1', 'i2', 'i3'])
    expect(new Set(cards.map((c) => c.y)).size).toBe(2)
    expect(new Set((await db.outbox.toArray()).map((e) => e.batch)).size).toBe(1)
  })
})
