import { shallowRef } from 'vue'
import { SKIP_WAITING, SYNC_MESSAGE, SYNC_TAG } from './swRules'

/** A new version that waits to take over: App.vue shows the "New version available – Reload" toast while it is set. */
export const waitingWorker = shallowRef<ServiceWorker | null>(null)

/** The page event the worker's Background Sync turns into (phase 2's sync engine listens). */
export const syncEventName = 'pgpm:sync'

let reloadOnTakeover = false

/**
 * Registers /sw.js (docs/03 §11.1, PLM's registerServiceWorker): scope /, and `updateViaCache: 'none'`, so the update
 * check never comes from the HTTP cache. A new worker that installs while an old one controls the page waits for the
 * toast; Reload posts 'skip-waiting' and the page reloads once the new worker has taken over. Only in a build: Vite's dev
 * server has no worker.
 */
export async function registerServiceWorker(): Promise<ServiceWorkerRegistration | undefined> {
  if (!import.meta.env.PROD || typeof navigator === 'undefined' || !('serviceWorker' in navigator)) {
    return undefined
  }
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (reloadOnTakeover) {
      reloadOnTakeover = false
      location.reload()
    }
  })
  navigator.serviceWorker.addEventListener('message', (event: MessageEvent) => {
    if ((event.data as { type?: string } | null)?.type === SYNC_MESSAGE) {
      window.dispatchEvent(new Event(syncEventName))
    }
  })
  try {
    const registration = await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' })
    const offer = (worker: ServiceWorker | null) => {
      if (worker && navigator.serviceWorker.controller) {
        waitingWorker.value = worker
      }
    }
    offer(registration.waiting)
    registration.addEventListener('updatefound', () => {
      const worker = registration.installing
      worker?.addEventListener('statechange', () => {
        if (worker.state === 'installed') offer(worker)
      })
    })
    // An open tab hears of a release within the hour, not only at its next navigation.
    setInterval(() => void registration.update().catch(() => {}), 60 * 60 * 1000)
    return registration
  } catch {
    // An insecure context or no support: the app still runs online.
    return undefined
  }
}

/** The toast's Reload: the waiting worker takes over, then the page reloads (controllerchange). */
export function applyUpdate() {
  const worker = waitingWorker.value
  if (!worker) {
    location.reload()
    return
  }
  reloadOnTakeover = true
  worker.postMessage(SKIP_WAITING)
}

/**
 * Asks the browser for a Background Sync (phase 2: the outbox isn't empty and a sync failed offline). Where it has none
 * (Firefox, Safari), false: the sync engine retries on `online` and on focus instead.
 */
export async function requestBackgroundSync(): Promise<boolean> {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) {
    return false
  }
  try {
    const registration = (await navigator.serviceWorker.ready) as ServiceWorkerRegistration & {
      sync?: { register(tag: string): Promise<void> }
    }
    if (!registration.sync) return false
    await registration.sync.register(SYNC_TAG)
    return true
  } catch {
    return false
  }
}
