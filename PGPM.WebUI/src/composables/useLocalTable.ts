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
 * - Sort strings `"field"` / `"-field"`, several comma-separated (`status,-updated_at`) with `multiSort`; search
 *   debounced 250 ms, normalised (lower case, no diacritics), every word must occur; a filter or search change goes back
 *   to page 1; `rowsPerPageOptions` 25/50/100/200.
 * - `syncUrl`: page, sort and search in the URL (`?page=2&sort=-title&q=web`), plus the view's `urlQuery`.
 * - `settingsKey` (`list:note`): the last search, sort, page size, the view's `remember()` filters, the `columns` and
 *   the saved view (`viewId`) are kept per user in `user_settings` (data model §13) and restored when the URL says
 *   nothing.
 * - `applyState` applies a saved view's definition (`{search, filters, sort, columns}`), `currentState` is what a view
 *   saves.
 */
export interface ListState {
  viewId?: string | null
  search: string
  sort: string[]
  pageSize?: number
  filters?: Record<string, unknown>
  columns?: string[]
}

/** A saved view's definition (data model §7.8): the list state without page size and view. */
export interface ViewState {
  search?: string
  filters?: Record<string, unknown>
  sort?: string[]
  columns?: string[]
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
  /** Sorting by several columns (PrimeVue's `sortMode: 'multiple'`: Ctrl/⌘-click adds a column). */
  multiSort?: boolean
  pageSize?: number
  syncUrl?: boolean
  urlQuery?: Ref<Record<string, string | undefined>>
  settingsKey?: string
  /** The filters to keep with the list state. */
  remember?: () => Record<string, unknown>
  /** The kept filters, when the state is restored (or a view applied). */
  restore?: (filters: Record<string, unknown>) => void
  /** The shown columns (ids, in order): remembered and set by a view. */
  columns?: Ref<string[]>
  /** The saved view the list shows: remembered. */
  viewId?: Ref<string | null>
  db?: () => PgpmDb
  repo?: Repo
}

/**
 * The last texts normalised (a list's search runs over the same texts on every keystroke), up to this many characters
 * (about 16 MB); then it starts afresh.
 */
const NORMALISED_MAX_CHARS = 8_000_000
let normalised = new Map<string, string>()
let normalisedChars = 0

/** Lower case, without diacritics: "Zürich" is found by "zurich". Remembered for the texts seen last (docs/07 §2). */
export function normalise(text: string): string {
  const known = normalised.get(text)
  if (known !== undefined) return known
  const result = text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
  if (normalisedChars + 2 * text.length > NORMALISED_MAX_CHARS) {
    normalised = new Map()
    normalisedChars = 0
  }
  normalised.set(text, result)
  normalisedChars += text.length + result.length
  return result
}

/** `localeCompare(…, undefined, { numeric, sensitivity: 'base' })`, made once: a collator per call is slow (docs/07 §2). */
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })

/** Order of two values: numbers and dates as such, text by the locale, empty values last. */
export function compareValues(a: unknown, b: unknown): number {
  const empty = (v: unknown) => v === null || v === undefined || v === ''
  if (empty(a) || empty(b)) return empty(a) === empty(b) ? 0 : empty(a) ? 1 : -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (typeof a === 'boolean' && typeof b === 'boolean') return a === b ? 0 : a ? -1 : 1
  return collator.compare(String(a), String(b))
}

/** The sort keys of a sort string: `"status,-updated_at"` → `["status", "-updated_at"]`. */
export function sortKeys(sort: string): string[] {
  return sort
    .split(',')
    .map((k) => k.trim())
    .filter((k) => k && k !== '-')
}

/** What {@link matchRows} filters and sorts by: `useLocalTable`'s options and its state. */
export interface MatchSpec<T> {
  /** The search's words, normalised. */
  words: readonly string[]
  filters: Record<string, unknown>
  match?: (row: T, filters: Record<string, unknown>) => boolean
  text: (row: T) => string
  /** The sort keys (`title`, `-updated_at`). */
  keys: readonly string[]
  sortValue?: (row: T, field: string) => unknown
  lead?: (a: T, b: T) => number
}

/**
 * The rows that pass the filters and contain every word, sorted (ties by id): the list's work on every change of the
 * data, the search, the filters or the sort – in memory, for 10,000 rows (docs/07 §2).
 */
export function matchRows<T extends { id: string }>(all: readonly T[], spec: MatchSpec<T>): T[] {
  const { words, filters, match, text } = spec
  const rows = all.filter((row) => {
    if (match && !match(row, filters)) return false
    if (!words.length) return true
    const found = normalise(text(row))
    return words.every((w) => found.includes(w))
  })
  const order = spec.keys.map((k) => ({ field: k.replace(/^-/, ''), direction: k.startsWith('-') ? -1 : 1 }))
  const value = spec.sortValue ?? ((row: T, f: string) => (row as Record<string, unknown>)[f])
  const lead = spec.lead
  // Each row's sort values once, not twice per comparison (a person's name, a score: docs/07 §2).
  const keyed = rows.map((row) => ({ row, values: order.map((o) => value(row, o.field)) }))
  keyed.sort((a, b) => {
    const first = lead?.(a.row, b.row) ?? 0
    if (first) return first
    for (let i = 0; i < order.length; i++) {
      const c = order[i]!.direction * compareValues(a.values[i], b.values[i])
      if (c) return c
    }
    return a.row.id < b.row.id ? -1 : a.row.id > b.row.id ? 1 : 0
  })
  return keyed.map((k) => k.row)
}

export type SortEvent = {
  sortField?: unknown
  sortOrder?: number | null
  multiSortMeta?: { field?: unknown; order?: number | null }[] | null
}

export function useLocalTable<T extends { id: string }>(
  options: Options<T>,
): Omit<TableSource<T>, 'onSort'> & {
  /** PrimeVue's sort event: one column, or several (`multiSortMeta`). */
  onSort(event: SortEvent): void
  sort: Ref<string>
  search: Ref<string>
  params: Readonly<Ref<Record<string, unknown>>>
  /** Every row that passes search and filters (all pages). */
  matching: Readonly<Ref<T[]>>
  loaded: Readonly<Ref<boolean>>
  /** The remembered state was restored (or there was none): a view may be applied now. */
  restored: Readonly<Ref<boolean>>
  /** A saved view's definition becomes the list's state. */
  applyState: (state: ViewState) => void
  /** The state a saved view keeps. */
  currentState: () => Required<ViewState>
} {
  const route = options.syncUrl ? useRoute() : null
  const router = options.syncUrl ? useRouter() : null
  const fromUrl = route?.query ?? {}
  const defaultPageSize = options.pageSize ?? 50
  const defaultSort = options.defaultSort ?? ''
  const page = ref(Number(fromUrl.page) || 1)
  const pageSize = ref(Number(fromUrl.pageSize) || defaultPageSize)
  const sort = ref(typeof fromUrl.sort === 'string' ? fromUrl.sort : defaultSort)
  const search = ref(typeof fromUrl.q === 'string' ? fromUrl.q : '')
  const debounced = ref(search.value)
  const restored = ref(!options.settingsKey)
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
  const keys = computed(() => {
    const all = sortKeys(sort.value)
    return options.multiSort ? all : all.slice(0, 1)
  })

  const matching = computed(() =>
    matchRows(live.value.value, {
      words: normalise(debounced.value.trim()).split(/\s+/).filter(Boolean),
      filters: options.filters?.value ?? {},
      match: options.match,
      text: options.text,
      keys: keys.value,
      sortValue: options.sortValue,
      lead: options.lead,
    }),
  )
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
      set('sort', sort.value, defaultSort)
      set('q', debounced.value.trim(), '')
      void router.replace({ query })
    })
  }

  function applyState(state: ViewState) {
    clearTimeout(timer)
    search.value = state.search ?? ''
    debounced.value = search.value
    sort.value = state.sort?.length ? state.sort.join(',') : defaultSort
    options.restore?.(state.filters ?? {})
    if (options.columns && state.columns?.length) options.columns.value = [...state.columns]
    page.value = 1
  }

  function currentState(): Required<ViewState> {
    return {
      search: search.value.trim(),
      filters: options.remember?.() ?? {},
      sort: sortKeys(sort.value),
      columns: [...(options.columns?.value ?? [])],
    }
  }

  if (options.settingsKey) {
    rememberState(
      options,
      { search, debounced, sort, pageSize, page },
      !!Object.keys(fromUrl).length,
      () => (restored.value = true),
    )
  }

  const query = {
    isError: computed(() => live.error.value !== null),
    data: computed(() => (live.loaded.value ? { items: items.value } : undefined)),
    error: computed(() => live.error.value),
    isFetching: computed(() => !live.loaded.value),
    refetch: () => live.refresh(),
  }

  const tableProps = computed(() => {
    const shared = {
      value: items.value,
      lazy: true,
      paginator: total.value > pageSize.value,
      rows: pageSize.value,
      first: (page.value - 1) * pageSize.value,
      totalRecords: total.value,
      loading: !live.loaded.value,
      rowsPerPageOptions: [25, 50, 100, 200],
    }
    if (options.multiSort) {
      return {
        ...shared,
        sortMode: 'multiple',
        removableSort: true,
        multiSortMeta: keys.value.map((k) => ({ field: k.replace(/^-/, ''), order: k.startsWith('-') ? -1 : 1 })),
      }
    }
    return {
      ...shared,
      sortField: sort.value.replace(/^-/, '') || undefined,
      sortOrder: sort.value ? (sort.value.startsWith('-') ? -1 : 1) : 0,
    }
  })

  function onPage(event: { page: number; rows: number }) {
    page.value = event.page + 1
    pageSize.value = event.rows
  }

  function onSort(event: SortEvent) {
    if (options.multiSort && Array.isArray(event.multiSortMeta)) {
      const next = event.multiSortMeta
        .filter((m) => typeof m.field === 'string' && m.field && m.order)
        .map((m) => `${m.order === -1 ? '-' : ''}${String(m.field)}`)
      sort.value = next.length ? next.join(',') : defaultSort
    } else {
      const field = typeof event.sortField === 'string' ? event.sortField : ''
      sort.value = field ? `${event.sortOrder === -1 ? '-' : ''}${field}` : defaultSort
    }
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
    restored,
    applyState,
    currentState,
  }
}

/** The list state in `user_settings` (`list:<type>`): restored once, then written 1 s after it settles. */
function rememberState<T>(
  options: Options<T>,
  state: { search: Ref<string>; debounced: Ref<string>; sort: Ref<string>; pageSize: Ref<number>; page: Ref<number> },
  fromUrl: boolean,
  done: () => void,
) {
  const db = options.db ?? localDb
  const repo = options.repo ?? appRepo
  const key = options.settingsKey!
  let restored = false
  let timer: ReturnType<typeof setTimeout> | undefined

  const current = (): ListState => ({
    ...(options.viewId ? { viewId: options.viewId.value } : {}),
    search: state.debounced.value.trim(),
    sort: sortKeys(state.sort.value),
    pageSize: state.pageSize.value,
    filters: options.remember?.() ?? {},
    ...(options.columns ? { columns: [...options.columns.value] } : {}),
  })

  async function find() {
    return (await db().synced('user_settings').where('key').equals(key).toArray()).find((r) => r.deleted !== true)
  }

  void (async () => {
    try {
      const row = await find()
      const value = row?.value as Partial<ListState> | undefined
      // The columns are never in the URL: they come back either way.
      if (value && options.columns && Array.isArray(value.columns) && value.columns.length) {
        options.columns.value = value.columns.filter((c): c is string => typeof c === 'string')
      }
      if (value && !fromUrl) {
        state.search.value = value.search ?? ''
        state.debounced.value = value.search ?? ''
        if (value.sort?.length) state.sort.value = value.sort.join(',')
        if (value.pageSize) state.pageSize.value = value.pageSize
        if (value.filters) options.restore?.(value.filters)
        if (options.viewId && value.viewId !== undefined) options.viewId.value = value.viewId ?? null
        state.page.value = 1
      }
    } catch {
      // No local data: the defaults.
    }
    restored = true
    done()
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
