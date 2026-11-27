import { ApiError, readCookie, reportApiError } from './client'

/**
 * Calls of the v1 API that are not in openapi.json yet (phase 2: sync, sharing, trash, history, editing): plain fetch
 * with the same rules as `call()` – same origin, cookie, the CSRF header on unsafe methods, an {@link ApiError} for a
 * failure (status 0 `network` when the server wasn't reached, also on a timeout), the error listeners told (a 401 ends
 * the session). They give way to the generated types once the server's OpenAPI document has them.
 */
export interface RawOptions {
  body?: unknown
  query?: Record<string, string | number | boolean | null | undefined>
  signal?: AbortSignal
  /** Milliseconds until the call counts as unanswered (offline); default 15 s. */
  timeout?: number
}

export async function rawCall<T>(method: string, path: string, options: RawOptions = {}): Promise<T> {
  const url = new URL(path, typeof location !== 'undefined' ? location.origin : 'http://localhost')
  for (const [key, value] of Object.entries(options.query ?? {})) {
    if (value !== undefined && value !== null && value !== '') url.searchParams.set(key, String(value))
  }
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    const token = typeof document !== 'undefined' ? readCookie('XSRF-TOKEN') : null
    if (token) headers['X-XSRF-TOKEN'] = token
  }
  const timeout = new AbortController()
  const timer = setTimeout(() => timeout.abort(), options.timeout ?? 15_000)
  const abort = () => timeout.abort()
  options.signal?.addEventListener('abort', abort)
  let response: Response
  try {
    response = await globalThis.fetch(url, {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: timeout.signal,
    })
  } catch {
    const error = new ApiError(0, undefined)
    reportApiError(error, url.pathname)
    throw error
  } finally {
    clearTimeout(timer)
    options.signal?.removeEventListener('abort', abort)
  }
  const text = await response.text().catch(() => '')
  let body: unknown = undefined
  try {
    body = text ? JSON.parse(text) : undefined
  } catch {
    body = undefined
  }
  if (!response.ok) {
    const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
    const error = new ApiError(response.status, body, Number.isNaN(retryAfter) ? undefined : retryAfter)
    reportApiError(error, url.pathname)
    throw error
  }
  return body as T
}
