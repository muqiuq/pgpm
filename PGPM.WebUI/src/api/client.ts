import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

/** The v1 contract's types, generated from openapi.json (tools/npm.sh run gen:api, docs/03 §3.3). */
export type Schemas = components['schemas']

/**
 * A v1 error: the ProblemDetails `code` (docs/03 §3.3), the HTTP status, and for validation_failed the messages per
 * field (JSON names).
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly detail: string | undefined
  readonly errors: Record<string, string[]>
  /** Seconds, from Retry-After (locked_out, rate_limited). */
  readonly retryAfter: number | undefined
  /** The whole ProblemDetails body, for the members a code adds (missing_right's area and level). */
  readonly body: Record<string, unknown>

  constructor(status: number, body: unknown, retryAfter?: number) {
    const problem = (typeof body === 'object' && body !== null ? body : {}) as Record<string, unknown>
    const code = typeof problem.code === 'string' ? problem.code : status === 0 ? 'network' : 'error'
    const detail = typeof problem.detail === 'string' ? problem.detail : undefined
    const title = typeof problem.title === 'string' ? problem.title : undefined
    super(detail ?? title ?? (status === 0 ? 'The server cannot be reached.' : `Request failed (${status}).`))
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.detail = detail
    this.errors = (typeof problem.errors === 'object' && problem.errors !== null ? problem.errors : {}) as Record<
      string,
      string[]
    >
    this.retryAfter = retryAfter
    this.body = problem
  }

  /** The first message for a field, for showing it under the input. */
  field(name: string): string | undefined {
    return this.errors[name]?.[0]
  }
}

/** A cookie's value (document.cookie), decoded. */
export function readCookie(name: string): string | null {
  for (const part of document.cookie.split(';')) {
    const [key, ...rest] = part.trim().split('=')
    if (key === name) {
      return decodeURIComponent(rest.join('='))
    }
  }
  return null
}

const unsafe = (method: string) => !['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase())

/** Unsafe calls echo the XSRF-TOKEN cookie (issued by /auth/me and the login) in X-XSRF-TOKEN. */
export const csrf: Middleware = {
  onRequest({ request }) {
    if (unsafe(request.method)) {
      const token = readCookie('XSRF-TOKEN')
      if (token) {
        request.headers.set('X-XSRF-TOKEN', token)
      }
    }
    return request
  },
}

// Absolute URLs on the page's origin; fetch is looked up per call (tests replace it).
export const client = createClient<paths>({
  baseUrl: typeof location !== 'undefined' ? location.origin : '',
  credentials: 'same-origin',
  fetch: (request) => globalThis.fetch(request),
})
client.use(csrf)

type Listener = (error: ApiError, path: string) => void
const listeners = new Set<Listener>()

/**
 * Called for every failed call, before the caller sees it: sessionHandling.ts sends a 401 to the sign-in and a
 * 403 must_change_password / totp_setup_required to the matching screen.
 */
export function onApiError(listener: Listener): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/**
 * The data of an openapi-fetch call, or an {@link ApiError}. A call the server never answered
 * (offline, proxy down) is an ApiError with status 0 and code "network".
 */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  let result: { data?: T; error?: unknown; response: Response }
  try {
    result = await request
  } catch {
    const error = new ApiError(0, undefined)
    listeners.forEach((l) => l(error, ''))
    throw error
  }
  const { data, error, response } = result
  if (!response.ok) {
    const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
    const failure = new ApiError(response.status, error, Number.isNaN(retryAfter) ? undefined : retryAfter)
    const path = new URL(response.url || '/', 'http://x').pathname
    listeners.forEach((l) => l(failure, path))
    throw failure
  }
  return data as T
}

/** What {@link fetchApi} sends: a JSON body (any object), or raw bytes. */
export interface FetchOptions {
  method?: string
  /** An object or array goes as JSON; a Blob, ArrayBuffer or typed array as raw bytes. */
  body?: unknown
  signal?: AbortSignal
  headers?: Record<string, string>
}

function isRaw(body: unknown): body is BodyInit {
  return (
    body instanceof ArrayBuffer ||
    ArrayBuffer.isView(body) ||
    (typeof Blob !== 'undefined' && body instanceof Blob) ||
    typeof body === 'string'
  )
}

/**
 * A v1 call outside openapi-fetch – raw bytes (file chunks and downloads, docs/03 §10.2) – with the rules of
 * {@link call}: same origin, the CSRF header on unsafe methods, an
 * {@link ApiError} for an error answer (status 0: not reached), the error listeners told. Answers the Response.
 */
export async function fetchApi(path: string, options: FetchOptions = {}): Promise<Response> {
  const method = (options.method ?? 'GET').toUpperCase()
  const headers = new Headers(options.headers)
  let body: BodyInit | undefined
  if (options.body !== undefined) {
    if (isRaw(options.body)) {
      body = options.body
      if (!headers.has('Content-Type')) headers.set('Content-Type', 'application/octet-stream')
    } else {
      body = JSON.stringify(options.body)
      headers.set('Content-Type', 'application/json')
    }
  }
  if (unsafe(method) && typeof document !== 'undefined') {
    const token = readCookie('XSRF-TOKEN')
    if (token) headers.set('X-XSRF-TOKEN', token)
  }
  const base = typeof location !== 'undefined' ? location.origin : 'http://localhost'
  const url = new URL(path, base)
  let response: Response
  try {
    response = await globalThis.fetch(url.toString(), {
      method,
      headers,
      body,
      signal: options.signal,
      credentials: 'same-origin',
      cache: 'no-store',
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    const error = new ApiError(0, undefined)
    listeners.forEach((l) => l(error, url.pathname))
    throw error
  }
  if (!response.ok) {
    let problem: unknown
    try {
      problem = await response.json()
    } catch {
      problem = undefined
    }
    const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
    const failure = new ApiError(response.status, problem, Number.isNaN(retryAfter) ? undefined : retryAfter)
    listeners.forEach((l) => l(failure, url.pathname))
    throw failure
  }
  return response
}

/** {@link fetchApi}, answering the JSON body (undefined for 204 or an empty body). */
export async function fetchJson<T>(path: string, options: FetchOptions = {}): Promise<T> {
  const response = await fetchApi(path, options)
  const text = response.status === 204 ? '' : await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}
