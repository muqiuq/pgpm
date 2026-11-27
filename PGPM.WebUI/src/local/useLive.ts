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
 * While the sync pulls (the app's engine says so, boot.ts), the live queries re-run together, in bursts: a change only
 * holds a query back, and the held ones run at once when the last burst has been over for `PULL_PAUSE` ms (or twice
 * as long as it took, if that is longer) and right after the pull – instead of each after every page's transaction,
 * whose next one would wait for their reads (docs/07 §2.3). Otherwise every change re-runs a query at once.
 */
const PULL_PAUSE = 500
let pulling = false
/** Real runs not ended yet (an end includes the rendering it caused), when the last burst began and ended. */
let busy = 0
let burstStart = 0
let quietSince = -Infinity
/** The pause after the last burst: PULL_PAUSE, or twice the burst's length. */
let pause = PULL_PAUSE
let timer: ReturnType<typeof setTimeout> | undefined
/** The live queries holding back a re-run, by their resume. */
const held = new Set<() => void>()

/** The sync pulls (or no longer): boot.ts, from the engine's status (`syncing`) in every tab. */
export function setLivePulling(on: boolean) {
  pulling = on
  if (!on) burst()
}

function burst() {
  clearTimeout(timer)
  timer = undefined
  for (const resume of [...held]) resume()
}

/** The next burst: when the last one has been over for its pause (none while one runs: its end schedules it). */
function scheduleBurst() {
  if (busy || !held.size) return
  clearTimeout(timer)
  timer = setTimeout(burst, Math.max(0, quietSince + pause - performance.now()))
}

function began() {
  if (!busy++) burstStart = performance.now()
}

function ended() {
  // After what the answer causes (Vue renders in microtasks): the next task.
  setTimeout(() => {
    if (--busy) return
    quietSince = performance.now()
    pause = Math.max(PULL_PAUSE, 2 * (quietSince - burstStart))
    scheduleBurst()
  })
}

/** A run that was held back: its answer isn't one. */
const HELD = Symbol('held')

/**
 * A Dexie query as a Vue ref (docs/03 §12.5): Dexie's `liveQuery` answers again whenever the data it read changes – in
 * this tab or another. `key` is reactive input of the query (a route's id): when it changes, the query starts over.
 * Unsubscribed with the calling component (or effect scope).
 *
 * While the sync pulls, re-runs are coalesced ({@link setLivePulling}): Dexie calls the query on a change; it reads
 * nothing then – it answers the last run's result, which Dexie drops when a change came during that run (so a list
 * still shows the pull's progress), or nothing new ({@link HELD}) – and that subscription observes nothing any more;
 * with the next burst a new subscription follows (one real run, observing again). A new key and `refresh` read at once.
 */
export function useLive<T>(query: () => T | Promise<T>, initial: T, key?: () => unknown): Live<T> {
  const value = shallowRef(initial) as ShallowRef<T>
  const loaded = ref(false)
  const error = shallowRef<unknown>(null)
  let subscription: Subscription | null = null
  /** A new subscription's first run always reads. */
  let fresh = true
  /** The subscription (counted), and the last result of a real run of it not answered yet. */
  let generation = 0
  let latest: T | typeof HELD = HELD

  function resume() {
    if (!held.delete(resume)) return
    subscribe()
  }

  const run = (): T | Promise<T> | typeof HELD => {
    if (!fresh && pulling) {
      held.add(resume)
      scheduleBurst()
      const answer = latest
      latest = HELD
      return answer
    }
    fresh = false
    began()
    const mine = generation
    let result: T | Promise<T>
    try {
      result = query()
    } catch (e) {
      ended()
      throw e
    }
    Promise.resolve(result).then((v) => {
      if (mine === generation) latest = v
      ended()
    }, ended)
    return result
  }
  // Dexie treats async queries (native awaits) apart: the wrapper is what the query is.
  const isAsync = (query as unknown as Record<symbol, unknown>)[Symbol.toStringTag] === 'AsyncFunction'
  const querier = isAsync ? async () => run() : run

  const subscribe = () => {
    held.delete(resume)
    subscription?.unsubscribe()
    fresh = true
    generation++
    latest = HELD
    subscription = liveQuery(querier).subscribe({
      next: (next) => {
        if (next === HELD) return
        latest = HELD
        value.value = next as T
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
  if (getCurrentScope()) {
    onScopeDispose(() => {
      held.delete(resume)
      subscription?.unsubscribe()
    })
  }
  return { value, loaded, error, refresh: subscribe }
}
