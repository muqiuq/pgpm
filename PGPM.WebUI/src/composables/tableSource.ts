import type { Ref } from 'vue'

/**
 * What `ListTable.vue` reads of a list (docs/03 §12.3): the shape `useServerTable` (online lists) and `useLocalTable`
 * (synced data) both return, so one table component serves both.
 */
export interface TableSource<T> {
  query: {
    isError: Readonly<Ref<boolean>>
    data: Readonly<Ref<unknown>>
    error: Readonly<Ref<unknown>>
    refetch: () => unknown
  }
  items: Readonly<Ref<T[]>>
  total: Readonly<Ref<number>>
  page: Ref<number>
  pageSize: Ref<number>
  tableProps: Readonly<Ref<Record<string, unknown>>>
  onPage(event: { page: number; rows: number }): void
  onSort(event: { sortField?: unknown; sortOrder?: number | null }): void
  goTo(page: number): void
}
