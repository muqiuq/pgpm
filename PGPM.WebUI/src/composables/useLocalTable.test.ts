import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { effectScope, ref, type EffectScope } from 'vue'
import type { PgpmDb } from '@/local/db'
import { Repo } from '@/local/repo'
import type { ItemRow } from '@/local/rows'
import { memoryDb, until } from '@/test/memoryDb'
import { compareValues, normalise, useLocalTable } from './useLocalTable'

type Note = Pick<ItemRow, 'id' | 'title' | 'description' | 'pinned' | 'updatedAt' | 'deleted'>

const note = (id: string, title: string, extra: Partial<Note> = {}): Note => ({
  id,
  title,
  description: null,
  pinned: false,
  updatedAt: `2026-11-0${id.slice(-1)}T00:00:00Z`,
  deleted: false,
  ...extra,
})

describe('useLocalTable (docs/03 §12.3): synced rows, searched, filtered, sorted and paged in memory', () => {
  let db: PgpmDb
  let repo: Repo
  let scopes: EffectScope[]

  beforeEach(async () => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => 'me' })
    scopes = []
    await db
      .synced('items')
      .bulkPut([
        note('n1', 'Zürich office'),
        note('n2', 'Budget 2027', { description: 'Numbers for Zurich' }),
        note('n3', 'Archive', { pinned: true }),
        note('n4', 'Deleted', { deleted: true }),
      ])
  })
  afterEach(async () => {
    for (const s of scopes) s.stop()
    await db.delete()
  })

  function table(pageSize = 2) {
    const scope = effectScope()
    scopes.push(scope)
    const onlyPinned = ref(false)
    const t = scope.run(() =>
      useLocalTable<Note>({
        rows: () => db.synced('items').toArray() as unknown as Promise<Note[]>,
        text: (n) => `${n.title} ${n.description ?? ''}`,
        filters: ref({}),
        match: (n) => !n.deleted && (!onlyPinned.value || n.pinned),
        lead: (a, b) => Number(b.pinned) - Number(a.pinned),
        defaultSort: '-updatedAt',
        pageSize,
        settingsKey: 'list:note',
        db: () => db,
        repo,
      }),
    )!
    return t
  }

  it('normalises search text and compares values with empty ones last', () => {
    expect(normalise('Zürich ÉTÉ')).toBe('zurich ete')
    expect([3, null, 1].sort(compareValues)).toEqual([1, 3, null])
    expect(['b', 'A', 'c'].sort(compareValues)).toEqual(['A', 'b', 'c'])
  })

  it('has the shape ListTable reads, with the pinned first and then the sort', async () => {
    const t = table()
    await until(() => t.loaded.value, 'the rows')
    expect(t.total.value).toBe(3)
    expect(t.items.value.map((n) => n.id)).toEqual(['n3', 'n2'])
    expect(t.tableProps.value).toMatchObject({ lazy: true, paginator: true, rows: 2, totalRecords: 3, sortOrder: -1 })
    expect(t.query.data.value).toBeTruthy()
    expect(t.query.isError.value).toBe(false)

    t.onPage({ page: 1, rows: 2 })
    expect(t.items.value.map((n) => n.id)).toEqual(['n1'])
    t.onSort({ sortField: 'title', sortOrder: 1 })
    expect(t.page.value).toBe(1)
    expect(t.items.value.map((n) => n.title)).toEqual(['Archive', 'Budget 2027'])
  })

  it('searches without diacritics, every word, back on page 1', async () => {
    const t = table()
    await until(() => t.loaded.value, 'the rows')
    t.goTo(2)
    t.search.value = 'zurich'
    await until(() => t.total.value === 2, 'the search')
    expect(t.page.value).toBe(1)
    t.search.value = 'zurich numbers'
    await until(() => t.total.value === 1, 'two words')
    expect(t.items.value[0]!.id).toBe('n2')
  })

  it('follows changes of the local data live', async () => {
    const t = table(50)
    await until(() => t.loaded.value, 'the rows')
    await repo.create('items', { id: 'n9', type: 'note', title: 'New one', updatedAt: '2026-12-01T00:00:00Z' })
    await until(() => t.total.value === 4, 'the new row')
    await repo.remove('items', 'n9')
    await until(() => t.total.value === 3, 'the removal')
  })

  it('remembers search and sort in user_settings and restores them for the next list', async () => {
    const t = table()
    await until(() => t.loaded.value, 'the rows')
    t.onSort({ sortField: 'title', sortOrder: -1 })
    t.search.value = 'b'
    await until(
      async () =>
        (await db.synced('user_settings').where('key').equals('list:note').first())?.value !== undefined &&
        JSON.stringify((await db.synced('user_settings').where('key').equals('list:note').first())!.value).includes(
          '-title',
        ),
      'the remembered state',
      4000,
    )
    const row = await db.synced('user_settings').where('key').equals('list:note').first()
    expect(row!.value).toMatchObject({ search: 'b', sort: ['-title'], pageSize: 2 })
    expect(await db.outbox.where('[table+id]').equals(['user_settings', row!.id]).count()).toBe(1)

    const next = table()
    await until(() => next.sort.value === '-title' && next.search.value === 'b', 'the restored state')
  })
})
