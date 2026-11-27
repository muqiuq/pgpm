import { describe, expect, it } from 'vitest'
import { bypasses } from './swRules'

describe("the service worker's rules (docs/03 §11.1)", () => {
  const origin = 'https://pgpm.example.com'
  const get = (path: string, cache = 'default') => bypasses({ method: 'GET', cache }, new URL(path, origin), origin)

  it('never handles the API, MCP, the OIDC callbacks or the health check', () => {
    for (const path of ['/api/v1/info', '/api/v1/sync/live', '/API/v1/info', '/mcp', '/signin-oidc', '/healthz']) {
      expect(get(path), path).toBe(true)
    }
  })

  it('handles the shell, its assets and deep links', () => {
    for (const path of ['/', '/index.html', '/assets/index-3f9a.js', '/projects/42', '/apis-of-the-world', '/mcpx']) {
      expect(get(path), path).toBe(false)
    }
  })

  it('leaves other methods, other origins and no-store requests alone', () => {
    expect(bypasses({ method: 'POST' }, new URL('/notes', origin), origin)).toBe(true)
    expect(bypasses({ method: 'GET' }, new URL('https://cdn.example.com/x.js'), origin)).toBe(true)
    expect(get('/assets/index-3f9a.js', 'no-store')).toBe(true)
  })
})
