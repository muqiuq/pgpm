/**
 * PGPM's service worker (docs/03 §11.1): PLM's behaviours on Workbox's precache. Built by vite-plugin-pwa
 * (injectManifest), which replaces self.__WB_MANIFEST with the build's files and their revisions.
 *
 * - Precache: every hashed asset, the fonts, the icons and index.html. Installing fails if one can't be cached, so a
 *   half-cached shell never takes over. One cache with per-URL revisions – never a cache named after the version
 *   (PLM's lesson: a stale worker deleted the full cache).
 * - Navigations: the network, raced against PAGE_ANSWER_MS; on a timeout, a network error or a 5xx the cached
 *   index.html, the late answer's body cancelled.
 * - Never handled: /api, /mcp, the OIDC callbacks, /healthz, anything but GET, `cache: 'no-store'` (swRules.ts).
 * - Updates: a new worker waits until the page's toast posts 'skip-waiting' (src/pwa/register.ts).
 * - Background Sync: the 'pgpm-sync' event is passed on to every open page as { type: 'pgpm-sync' }.
 */
import { clientsClaim } from 'workbox-core'
import {
  cleanupOutdatedCaches,
  getCacheKeyForURL,
  matchPrecache,
  precache,
  type PrecacheEntry,
} from 'workbox-precaching'
import { registerRoute } from 'workbox-routing'
import { bypasses, PAGE_ANSWER_MS, SKIP_WAITING, SYNC_MESSAGE, SYNC_TAG } from './pwa/swRules'

declare const self: ServiceWorkerGlobalScope & { __WB_MANIFEST: (string | PrecacheEntry)[] }

/** Background Sync's event (not in TypeScript's WebWorker library). */
interface SyncEvent extends ExtendableEvent {
  readonly tag: string
}

const shellUrl = '/index.html'

precache(self.__WB_MANIFEST)
cleanupOutdatedCaches()
// The first worker controls the page that registered it at once; later ones wait for the toast.
clientsClaim()

self.addEventListener('message', (event) => {
  if (event.data === SKIP_WAITING) {
    void self.skipWaiting()
  }
})

const after = (ms: number) => new Promise<null>((resolve) => setTimeout(() => resolve(null), ms))

/**
 * A page load (PLM's navigate()): the server's answer, unless there is none worth showing – then the cached app. None is
 * a network that fails or has not begun to answer within PAGE_ANSWER_MS; not worth showing is a 5xx (the proxy's 502
 * while the app restarts). Every other answer is shown as it is. The request is raced, not aborted; a late or 5xx answer
 * is let go and its body cancelled.
 */
async function navigate(request: Request): Promise<Response> {
  const network = fetch(request).catch(() => null)
  const answer = await Promise.race([network, after(PAGE_ANSWER_MS)])
  if (answer && answer.status < 500) {
    return answer
  }
  const shell = await matchPrecache(shellUrl)
  if (shell) {
    void network.then((late) => late?.body?.cancel()).catch(() => {})
    return shell
  }
  // Nothing cached: the network is all there is, its 5xx or its late answer.
  return answer ?? (await network) ?? Response.error()
}

registerRoute(
  ({ request, url }) => request.mode === 'navigate' && !bypasses(request, url, self.location.origin),
  ({ request }) => navigate(request),
)

// The precached files from the cache (hashed: they never change), the network only if the cache lost one.
registerRoute(
  ({ request, url }) => !bypasses(request, url, self.location.origin) && getCacheKeyForURL(url.href) !== undefined,
  async ({ request }) => (await matchPrecache(request)) ?? fetch(request),
)

self.addEventListener('sync', (event) => {
  const sync = event as SyncEvent
  if (sync.tag !== SYNC_TAG) {
    return
  }
  sync.waitUntil(
    (async () => {
      const clients = await self.clients.matchAll({ includeUncontrolled: true })
      for (const client of clients) {
        client.postMessage({ type: SYNC_MESSAGE })
      }
    })(),
  )
})
