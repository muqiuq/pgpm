import { liveQuery, type Subscription } from 'dexie'
import { getCurrentScope, onScopeDispose, ref, shallowRef, watch, type Ref, type ShallowRef } from 'vue'

export interface Live<T> {
  /** The query's latest answer (the initial value until the first). */
  value: ShallowRef<T>
  /** The first answer came. */
  loaded: Ref<boolean>
  error: ShallowRef<unknown>
  /** Runs the query again (a retry after an error). */
  refresh: () => void
}

/**
 * A Dexie query as a Vue ref (docs/03 §12.5): Dexie's `liveQuery` answers again whenever the data it read changes – in
 * this tab or another. `key` is reactive input of the query (a route's id): when it changes, the query starts over.
 * Unsubscribed with the calling component (or effect scope).
 */
export function useLive<T>(query: () => T | Promise<T>, initial: T, key?: () => unknown): Live<T> {
  const value = shallowRef(initial) as ShallowRef<T>
  const loaded = ref(false)
  const error = shallowRef<unknown>(null)
  let subscription: Subscription | null = null

  const subscribe = () => {
    subscription?.unsubscribe()
    subscription = liveQuery(query).subscribe({
      next: (next) => {
        value.value = next
        loaded.value = true
        error.value = null
      },
      error: (e: unknown) => {
        error.value = e
        loaded.value = true
      },
    })
  }

  if (key) {
    watch(
      key,
      () => {
        loaded.value = false
        subscribe()
      },
      { immediate: true },
    )
  } else {
    subscribe()
  }
  if (getCurrentScope()) onScopeDispose(() => subscription?.unsubscribe())
  return { value, loaded, error, refresh: subscribe }
}
