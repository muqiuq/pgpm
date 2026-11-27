/**
 * One sync runner across the tabs of a browser (docs/03 §11.3, §11.7): the tab that holds the Web Lock `pgpm-sync` is
 * the leader – it owns the HTTP loop and the live WebSocket; the others are passive and see changes through Dexie's
 * liveQuery. When the leader closes, the browser hands the lock to the next tab. Without Web Locks (old browsers),
 * every tab leads: more requests, still correct (mutations are idempotent).
 */

/** The part of `navigator.locks` used here. */
export interface LockManagerLike {
  request(
    name: string,
    options: { signal?: AbortSignal },
    callback: (lock: unknown) => Promise<unknown>,
  ): Promise<unknown>
}

export const lockName = 'pgpm-sync'

export interface Leadership {
  /** Gives the lock back (or stops waiting for it). */
  release(): void
}

/**
 * Calls `lead` once this tab holds the lock (at once without Web Locks); `lead` runs until `release()` or the tab
 * closes. `onWaiting` hears that another tab leads for now.
 */
export function requestLeadership(
  lead: () => void,
  options: { locks?: LockManagerLike | null; onWaiting?: () => void } = {},
): Leadership {
  const locks =
    options.locks !== undefined
      ? options.locks
      : typeof navigator !== 'undefined' && 'locks' in navigator
        ? (navigator.locks as unknown as LockManagerLike)
        : null
  if (!locks) {
    lead()
    return { release: () => undefined }
  }
  const abort = new AbortController()
  let releaseLock: (() => void) | null = null
  let held = false
  const waiting = setTimeout(() => {
    if (!held && !abort.signal.aborted) options.onWaiting?.()
  }, 0)
  locks
    .request(lockName, { signal: abort.signal }, () => {
      held = true
      clearTimeout(waiting)
      if (abort.signal.aborted) return Promise.resolve()
      lead()
      return new Promise<void>((resolve) => (releaseLock = resolve))
    })
    .catch(() => undefined)
  return {
    release() {
      clearTimeout(waiting)
      abort.abort()
      releaseLock?.()
      releaseLock = null
    },
  }
}
