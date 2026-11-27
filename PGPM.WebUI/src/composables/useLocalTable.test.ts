import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { computed, effectScope, ref, type EffectScope } from 'vue'
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

  /** A list with several sort columns, shown columns, a saved view and a filter. */
  function viewTable() {
    const scope = effectScope()
    scopes.push(scope)
    const pinned = ref(false)
    const columns = ref(['key', 'title'])
    const viewId = ref<string | null>(null)
    const t = scope.run(() =>
      useLocalTable<Note>({
        rows: () => db.synced('items').toArray() as unknown as Promise<Note[]>,
        text: (n) => n.title,
        filters: computed(() => ({ pinned: pinned.value || undefined })),
        match: (n, f) => !n.deleted && (!f.pinned || n.pinned),
        defaultSort: '-updatedAt',
        multiSort: true,
        pageSize: 50,
        settingsKey: 'list:idea',
        remember: () => ({ pinned: pinned.value }),
        restore: (f) => (pinned.value = f.pinned === true),
        columns,
        viewId,
        db: () => db,
        repo,
      }),
    )!
    return { t, pinned, columns, viewId }
  }

  it('sorts by several columns, as PrimeVue’s multiSortMeta says', async () => {
    await db
      .synced('items')
      .bulkPut([
        note('n5', 'Budget 2027', { pinned: true, updatedAt: '2026-11-01T00:00:00Z' }),
        note('n6', 'Archive', { pinned: true, updatedAt: '2026-11-02T00:00:00Z' }),
      ])
    const { t } = viewTable()
    await until(() => t.loaded.value, 'the rows')
    t.onSort({
      multiSortMeta: [
        { field: 'pinned', order: 1 },
        { field: 'title', order: -1 },
      ],
    })
    expect(t.sort.value).toBe('pinned,-title')
    // Pinned first (true before false), then the title descending, ties by id.
    expect(t.items.value.map((n) => n.id)).toEqual(['n5', 'n3', 'n6', 'n1', 'n2'])
    expect(t.tableProps.value).toMatchObject({
      sortMode: 'multiple',
      removableSort: true,
      multiSortMeta: [
        { field: 'pinned', order: 1 },
        { field: 'title', order: -1 },
      ],
    })
    t.onSort({ multiSortMeta: [] })
    expect(t.sort.value).toBe('-updatedAt')
  })

  it('applies a saved view’s definition, answers the current one, and remembers view and columns', async () => {
    const { t, pinned, columns, viewId } = viewTable()
    await until(() => t.restored.value && t.loaded.value, 'the rows')
    t.applyState({ search: 'zurich', filters: { pinned: false }, sort: ['title', '-updatedAt'], columns: ['title'] })
    viewId.value = 'v1'
    expect(t.search.value).toBe('zurich')
    expect(t.sort.value).toBe('title,-updatedAt')
    expect(columns.value).toEqual(['title'])
    expect(t.currentState()).toEqual({
      search: 'zurich',
      filters: { pinned: false },
      sort: ['title', '-updatedAt'],
      columns: ['title'],
    })
    pinned.value = true
    expect(t.currentState().filters).toEqual({ pinned: true })
    await until(
      async () =>
        ((await db.synced('user_settings').where('key').equals('list:idea').first())?.value as { viewId?: string })
          ?.viewId === 'v1',
      'the remembered view',
      4000,
    )
    const row = await db.synced('user_settings').where('key').equals('list:idea').first()
    expect(row!.value).toMatchObject({
      viewId: 'v1',
      search: 'zurich',
      sort: ['title', '-updatedAt'],
      columns: ['title'],
      filters: { pinned: true },
    })

    const next = viewTable()
    await until(() => next.t.restored.value, 'the restored state')
    expect(next.viewId.value).toBe('v1')
    expect(next.columns.value).toEqual(['title'])
    expect(next.pinned.value).toBe(true)
    expect(next.t.sort.value).toBe('title,-updatedAt')
  })
})
