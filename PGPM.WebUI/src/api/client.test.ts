import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, call, client, csrf, onApiError, readCookie } from './client'

function respond(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return vi.fn(async (request: Request) => {
    const response = new Response(body === undefined ? null : JSON.stringify(body), {
      status,
      headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
    })
    Object.defineProperty(response, 'url', { value: request.url })
    return response
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/'
})

/** The middleware on its own, without a server. */
async function throughCsrf(method: string): Promise<Request> {
  const request = new Request('http://localhost/api/v1/info', { method })
  const out = await csrf.onRequest!({ request } as Parameters<NonNullable<typeof csrf.onRequest>>[0])
  return (out as Request | undefined) ?? request
}

describe('the API client (docs/03 §3.3)', () => {
  it('echoes the XSRF-TOKEN cookie on unsafe calls only', async () => {
    document.cookie = 'XSRF-TOKEN=abc%2Fdef; path=/'
    expect((await throughCsrf('POST')).headers.get('X-XSRF-TOKEN')).toBe('abc/def')
    expect((await throughCsrf('DELETE')).headers.get('X-XSRF-TOKEN')).toBe('abc/def')
    expect((await throughCsrf('GET')).headers.get('X-XSRF-TOKEN')).toBeNull()
    expect(readCookie('XSRF-TOKEN')).toBe('abc/def')
  })

  it('maps ProblemDetails to an ApiError with the code, the field messages and Retry-After, and tells the listeners', async () => {
    vi.stubGlobal(
      'fetch',
      respond(
        400,
        { status: 400, title: 'Invalid', code: 'validation_failed', errors: { title: ['Required.'] } },
        { 'Retry-After': '240' },
      ),
    )
    const seen: [string, string][] = []
    const stop = onApiError((e, path) => seen.push([e.code, path]))

    const error = (await call(client.GET('/api/v1/info')).catch((e: unknown) => e)) as ApiError
    stop()

    expect(error).toBeInstanceOf(ApiError)
    expect([error.status, error.code, error.field('title'), error.field('other'), error.retryAfter]).toEqual([
      400,
      'validation_failed',
      'Required.',
      undefined,
      240,
    ])
    expect(seen).toEqual([['validation_failed', '/api/v1/info']])
  })

  it('turns a call the server never answered into a network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))),
    )
    const error = (await call(client.GET('/api/v1/info')).catch((e: unknown) => e)) as ApiError
    expect([error.status, error.code]).toEqual([0, 'network'])
  })

  it('returns the data of a success', async () => {
    vi.stubGlobal('fetch', respond(200, { name: 'PGPM', version: '0.1.0' }))
    await expect(call(client.GET('/api/v1/info'))).resolves.toEqual({ name: 'PGPM', version: '0.1.0' })
  })
})
