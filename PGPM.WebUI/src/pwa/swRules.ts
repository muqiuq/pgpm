/**
 * The service worker's rules (docs/03 §11.1), apart from src/sw.ts so the page and Vitest can use them too: plain
 * TypeScript, no DOM and no WebWorker types.
 */

/** A page load waits this long for the network before the cached shell opens (PLM's PAGE_ANSWER_MS). */
export const PAGE_ANSWER_MS = 4_000

/** Background Sync's tag (phase 2 registers it when the outbox can't be sent) … */
export const SYNC_TAG = 'pgpm-sync'

/** … and the message the worker then posts to every open page: `{ type: SYNC_MESSAGE }`. */
export const SYNC_MESSAGE = 'pgpm-sync'

/** What the page posts to a waiting worker to take over (the update toast's Reload). */
export const SKIP_WAITING = 'skip-waiting'

/** Never the worker's: the API (sync and the SSO start too), MCP, the OIDC callbacks and the health check. */
const networkOnly = ['/api', '/mcp', '/signin-oidc', '/signout-callback-oidc', '/healthz']

/**
 * Whether the service worker leaves a request alone (the browser fetches it as if there were no worker): anything but
 * GET, another origin, a `cache: 'no-store'` request, and the network-only paths above.
 */
export function bypasses(request: { method: string; cache?: string }, url: URL, origin: string): boolean {
  if (request.method !== 'GET' || url.origin !== origin || request.cache === 'no-store') {
    return true
  }
  const path = url.pathname.toLowerCase()
  return networkOnly.some((prefix) => path === prefix || path.startsWith(`${prefix}/`))
}
