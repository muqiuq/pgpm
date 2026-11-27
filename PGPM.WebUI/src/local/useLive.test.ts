import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { effectScope, ref, type EffectScope } from 'vue'
import { memoryDb, until } from '@/test/memoryDb'
import { applyChanges, syncScope } from './apply'
import type { PgpmDb } from './db'
import type { Row } from './outbox'
import { setLivePulling, useLive } from './useLive'

const note = (n: number): Row => ({
  id: `01970000-0000-7000-8000-${n.toString(16).padStart(12, '0')}`,
  type: 'note',
  title: `Note ${n}`,
  access: 'edit',
  deleted: false,
  rev: 1,
  seq: n,
})

const pause = (ms: number) => new Promise((r) => setTimeout(r, ms))

/**
 * The live queries while the sync pulls (docs/07 §2.3): a list open during a large first sync re-read all its rows after
 * every page's transaction, and the next page's write waited for those reads. Now, while pulling, the held queries
 * re-run together in bursts at least 500 ms apart and once right after the pull; every change at once otherwise.
 */
describe('useLive while the sync pulls', () => {
  let db: PgpmDb
  let scope: EffectScope
  let runs: number

  /** The notes list's query (ItemList.vue), counting its runs. */
  function notesList() {
    return scope.run(() =>
      useLive(async () => {
        runs++
        return await db.synced('items').where('type').equals('note').toArray()
      }, [] as Row[]),
    )!
  }

  /** A pull's page as the engine applies it: one transaction, the rows and the cursor. */
  async function page(rows: Row[]) {
    await db.transaction('rw', syncScope(db), async () => {
      await applyChanges(db, { items: rows }, [])
      await db.setMeta('cursor', Number(rows.at(-1)!.seq))
    })
  }

  beforeEach(() => {
    db = memoryDb()
    scope = effectScope()
    runs = 0
  })

  afterEach(async () => {
    setLivePulling(false)
    scope.stop()
    await db.delete()
  })

  it('re-runs at once on every change while the sync isn’t pulling', async () => {
    const live = notesList()
    await until(() => live.loaded.value, 'the first answer')
    for (let n = 1; n <= 3; n++) {
      await page([note(n)])
      await until(() => live.value.value.length === n, `note ${n}`, 400)
      // At once (Dexie may run it twice for the first page).
      expect(runs).toBeGreaterThanOrEqual(1 + n)
    }
  })

  it('coalesces the re-runs of a multi-page pull: at most one per 500 ms, one right after it', async () => {
    const live = notesList()
    await until(() => live.loaded.value, 'the first answer')
    runs = 0
    const pages = 16
    const started = performance.now()
    const seen: number[] = []
    setLivePulling(true)
    for (let p = 0; p < pages; p++) {
      await page(Array.from({ length: 20 }, (_, i) => note(p * 20 + i + 1)))
      await pause(80)
      seen.push(live.value.value.length)
    }
    const pulled = performance.now() - started
    const during = runs
    setLivePulling(false)
    await until(() => live.value.value.length === pages * 20, 'every note after the pull', 1000)
    // The first page's change soon, then bursts at least 500 ms apart; and one right after the pull (unless the last
    // burst came after the last page).
    expect(during).toBeLessThanOrEqual(1 + Math.ceil(pulled / 500))
    expect(runs - during).toBeLessThanOrEqual(1)
    // The list showed the pull's progress meanwhile, not only its end.
    expect(seen.some((n) => n > 0 && n < pages * 20)).toBe(true)

    // Without the coalescing (the same pull): a run per page.
    runs = 0
    for (let p = 0; p < pages; p++) {
      await page(Array.from({ length: 20 }, (_, i) => ({ ...note(p * 20 + i + 1), title: 'again', rev: 2 })))
      await pause(80)
    }
    await until(() => live.value.value.every((n) => n.title === 'again'), 'the second pull', 1000)
    expect(runs).toBeGreaterThanOrEqual(pages)
  })

  it('re-runs the held queries of a pull together, in bursts, not each on its own', async () => {
    const starts: Record<string, number[]> = { notes: [], all: [] }
    const counted = (name: string, read: () => Promise<Row[]>) =>
      scope.run(() =>
        useLive(async () => {
          starts[name]!.push(performance.now())
          return await read()
        }, [] as Row[]),
      )!
    const notes = counted('notes', () => db.synced('items').where('type').equals('note').toArray())
    const all = counted('all', () => db.synced('items').toArray())
    await until(() => notes.loaded.value && all.loaded.value, 'the first answers')
    starts.notes = []
    starts.all = []
    setLivePulling(true)
    for (let p = 0; p < 12; p++) {
      await page(Array.from({ length: 10 }, (_, i) => note(p * 10 + i + 1)))
      await pause(70)
    }
    setLivePulling(false)
    await until(() => notes.value.value.length === 120 && all.value.value.length === 120, 'both after the pull', 1000)
    // Each burst runs both (within a few ms), so the pull's pages wait for one burst, not for each query's own.
    expect(starts.notes.length).toBe(starts.all.length)
    expect(starts.notes.length).toBeLessThan(12)
    starts.notes.forEach((at, i) => expect(Math.abs(at - starts.all[i]!)).toBeLessThan(30))
  })

  it('keeps observing what a held-back query reads after an await, and starts over at once for a new key', async () => {
    await db.synced('statuses').put({ id: 's1', itemType: 'note', name: 'Open', deleted: false, rev: 1 })
    const type = ref('note')
    const live = scope.run(() =>
      useLive(
        async () => {
          runs++
          const items = await db.synced('items').where('type').equals(type.value).toArray()
          const statuses = await db.synced('statuses').toArray()
          return `${items.length} ${type.value}s, ${statuses.map((s) => s.name).join()}`
        },
        '',
        () => type.value,
      ),
    )!
    await until(() => live.value.value === '0 notes, Open', 'the first answer')
    setLivePulling(true)
    await page([note(1)])
    await page([note(2)])
    await until(() => live.value.value.startsWith('1 note') || live.value.value.startsWith('2 notes'), 'page 1')
    // A new key during the pull reads at once (not after the interval).
    const before = runs
    type.value = 'idea'
    await until(() => live.value.value === '0 ideas, Open', 'the new key', 300)
    expect(runs).toBe(before + 1)
    type.value = 'note'
    await until(() => live.value.value === '2 notes, Open', 'the notes again', 300)
    setLivePulling(false)

    // The second table, read after an await, is still observed – after held-back runs too.
    setLivePulling(true)
    await page([note(3)])
    await page([note(4)])
    setLivePulling(false)
    await until(() => live.value.value === '4 notes, Open', 'after the pull', 1000)
    await db.synced('statuses').put({ id: 's1', itemType: 'note', name: 'Done', deleted: false, rev: 2 })
    await until(() => live.value.value === '4 notes, Done', 'the status change', 400)
  })
})
