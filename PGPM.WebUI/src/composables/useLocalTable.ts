import { computed, ref, watch, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { localDb, type PgpmDb } from '@/local/db'
import { repo as appRepo, type Repo } from '@/local/repo'
import { useLive } from '@/local/useLive'
import type { TableSource } from './tableSource'

/**
 * A list of synced data as a PrimeVue DataTable (docs/03 §12.3): the same return shape as `useServerTable`, so
 * `ListTable.vue`, `FilterBar.vue` and the column widths work unchanged – but the rows come from IndexedDB (a Dexie
 * `liveQuery`, so they follow every change, in all tabs) and search, filter, sort and paging run in memory, offline.
 *
 * - Sort strings `"field"` / `"-field"`; search debounced 250 ms, normalised (lower case, no diacritics), every word must
 *   occur; a filter or search change goes back to page 1; `rowsPerPageOptions` 25/50/100/200.
 * - `syncUrl`: page, sort and search in the URL (`?page=2&sort=-title&q=web`), plus the view's `urlQuery`.
 * - `settingsKey` (`list:note`): the last search, sort, page size and the view's `remember()` state are kept per user
 *   in `user_settings` and restored when the URL says nothing (data model §13).
 */
export interface ListState {
  search: string
  sort: string[]
  pageSize?: number
  filters?: Record<string, unknown>
}

interface Options<T> {
  /** All rows of the list (re-run when the data changes). */
  rows: () => Promise<T[]>
  /** The text a row is found by (key, title, description …). */
  text: (row: T) => string
  /** The view's filters (reactive); a change goes back to page 1. */
  filters?: Ref<Record<string, unknown>>
  /** Whether a row passes the filters. */
  match?: (row: T, filters: Record<string, unknown>) => boolean
  /** The value a column sorts by; default the row's member. */
  sortValue?: (row: T, field: string) => unknown
  /** A comparison before the chosen sort (pinned notes first). */
  lead?: (a: T, b: T) => number
  defaultSort?: string
  pageSize?: number
  syncUrl?: boolean
  urlQuery?: Ref<Record<string, string | undefined>>
  settingsKey?: string
  /** The filters to keep with the list state. */
  remember?: () => Record<string, unknown>
  /** The kept filters, when the state is restored. */
  restore?: (filters: Record<string, unknown>) => void
  db?: () => PgpmDb
  repo?: Repo
}

/** Lower case, without diacritics: "Zürich" is found by "zurich". */
export function normalise(text: string): string {
  return text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
}

/** Order of two values: numbers and dates as such, text by the locale, empty values last. */
export function compareValues(a: unknown, b: unknown): number {
  const empty = (v: unknown) => v === null || v === undefined || v === ''
  if (empty(a) || empty(b)) return empty(a) === empty(b) ? 0 : empty(a) ? 1 : -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (typeof a === 'boolean' && typeof b === 'boolean') return a === b ? 0 : a ? -1 : 1
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' })
}

export function useLocalTable<T extends { id: string }>(
  options: Options<T>,
): TableSource<T> & {
  sort: Ref<string>
  search: Ref<string>
  params: Readonly<Ref<Record<string, unknown>>>
  /** Every row that passes search and filters (all pages). */
  matching: Readonly<Ref<T[]>>
  loaded: Readonly<Ref<boolean>>
} {
  const route = options.syncUrl ? useRoute() : null
  const router = options.syncUrl ? useRouter() : null
  const fromUrl = route?.query ?? {}
  const defaultPageSize = options.pageSize ?? 50
  const page = ref(Number(fromUrl.page) || 1)
  const pageSize = ref(Number(fromUrl.pageSize) || defaultPageSize)
  const sort = ref(typeof fromUrl.sort === 'string' ? fromUrl.sort : (options.defaultSort ?? ''))
  const search = ref(typeof fromUrl.q === 'string' ? fromUrl.q : '')
  const debounced = ref(search.value)
  let timer: ReturnType<typeof setTimeout> | undefined
  watch(search, (value) => {
    clearTimeout(timer)
    timer = setTimeout(() => {
      debounced.value = value
      page.value = 1
    }, 250)
  })
  if (options.filters) watch(options.filters, () => (page.value = 1), { deep: true })

  const live = useLive(options.rows, [] as T[])

  const matching = computed(() => {
    const words = normalise(debounced.value.trim()).split(/\s+/).filter(Boolean)
    const filters = options.filters?.value ?? {}
    let rows = live.value.value.filter(
      (row) =>
        (!options.match || options.match(row, filters)) &&
        (!words.length || words.every((w) => normalise(options.text(row)).includes(w))),
    )
    const field = sort.value.replace(/^-/, '')
    const direction = sort.value.startsWith('-') ? -1 : 1
    const value = options.sortValue ?? ((row: T, f: string) => (row as Record<string, unknown>)[f])
    rows = [...rows].sort(
      (a, b) =>
        (options.lead?.(a, b) ?? 0) ||
        (field ? direction * compareValues(value(a, field), value(b, field)) : 0) ||
        (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
    )
    return rows
  })
  const total = computed(() => matching.value.length)
  const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
  watch(pages, (n) => {
    if (live.loaded.value && page.value > n) page.value = n
  })
  const items = computed(() => matching.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))

  const params = computed(() => ({
    page: page.value,
    pageSize: pageSize.value,
    sort: sort.value || undefined,
    search: debounced.value.trim() || undefined,
    ...(options.filters?.value ?? {}),
  }))

  if (options.syncUrl && route && router) {
    watch([params, () => options.urlQuery?.value], () => {
      const query = { ...route.query }
      for (const [k, v] of Object.entries(options.urlQuery?.value ?? {})) {
        if (v === undefined) delete query[k]
        else query[k] = v
      }
      const set = (k: string, v: unknown, empty: unknown) => {
        if (v === undefined || v === empty || v === '') delete query[k]
        else query[k] = String(v)
      }
      set('page', page.value, 1)
      set('pageSize', pageSize.value, defaultPageSize)
      set('sort', sort.value, options.defaultSort ?? '')
      set('q', debounced.value.trim(), '')
      void router.replace({ query })
    })
  }

  if (options.settingsKey)
    rememberState(options, { search, debounced, sort, pageSize, page }, !!Object.keys(fromUrl).length)

  const query = {
    isError: computed(() => live.error.value !== null),
    data: computed(() => (live.loaded.value ? { items: items.value } : undefined)),
    error: computed(() => live.error.value),
    isFetching: computed(() => !live.loaded.value),
    refetch: () => live.refresh(),
  }

  const tableProps = computed(() => ({
    value: items.value,
    lazy: true,
    paginator: total.value > pageSize.value,
    rows: pageSize.value,
    first: (page.value - 1) * pageSize.value,
    totalRecords: total.value,
    loading: !live.loaded.value,
    sortField: sort.value.replace(/^-/, '') || undefined,
    sortOrder: sort.value ? (sort.value.startsWith('-') ? -1 : 1) : 0,
    rowsPerPageOptions: [25, 50, 100, 200],
  }))

  function onPage(event: { page: number; rows: number }) {
    page.value = event.page + 1
    pageSize.value = event.rows
  }

  function onSort(event: { sortField?: unknown; sortOrder?: number | null }) {
    const field = typeof event.sortField === 'string' ? event.sortField : ''
    sort.value = field ? `${event.sortOrder === -1 ? '-' : ''}${field}` : (options.defaultSort ?? '')
    page.value = 1
  }

  function goTo(target: number) {
    page.value = Math.min(Math.max(1, target), pages.value)
  }

  return {
    query,
    items,
    total,
    page,
    pageSize,
    sort,
    search,
    params,
    tableProps,
    onPage,
    onSort,
    goTo,
    matching,
    loaded: live.loaded,
  }
}

/** The list state in `user_settings` (`list:<type>`): restored once, then written 1 s after it settles. */
function rememberState<T>(
  options: Options<T>,
  state: { search: Ref<string>; debounced: Ref<string>; sort: Ref<string>; pageSize: Ref<number>; page: Ref<number> },
  fromUrl: boolean,
) {
  const db = options.db ?? localDb
  const repo = options.repo ?? appRepo
  const key = options.settingsKey!
  let restored = false
  let timer: ReturnType<typeof setTimeout> | undefined

  const current = (): ListState => ({
    search: state.debounced.value.trim(),
    sort: state.sort.value ? [state.sort.value] : [],
    pageSize: state.pageSize.value,
    filters: options.remember?.() ?? {},
  })

  async function find() {
    return (await db().synced('user_settings').where('key').equals(key).toArray()).find((r) => r.deleted !== true)
  }

  void (async () => {
    try {
      const row = await find()
      const value = row?.value as Partial<ListState> | undefined
      if (value && !fromUrl) {
        state.search.value = value.search ?? ''
        state.debounced.value = value.search ?? ''
        if (value.sort?.length) state.sort.value = value.sort[0]!
        if (value.pageSize) state.pageSize.value = value.pageSize
        if (value.filters) options.restore?.(value.filters)
        state.page.value = 1
      }
    } catch {
      // No local data: the defaults.
    }
    restored = true
  })()

  watch(
    () => JSON.stringify(current()),
    () => {
      if (!restored) return
      clearTimeout(timer)
      timer = setTimeout(async () => {
        try {
          const value = current()
          const row = await find()
          if (!row) await repo.create('user_settings', { key, value })
          else if (JSON.stringify(row.value) !== JSON.stringify(value))
            await repo.patch('user_settings', row.id, { value })
        } catch {
          // Remembering is a convenience.
        }
      }, 1000)
    },
  )
}
