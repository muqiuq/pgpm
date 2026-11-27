import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeResponse } from '@/api/auth'
import { catalogue } from '@/test/catalogue'
import { sessionCacheKey, useSession } from './session'

const me: MeResponse = {
  user: {
    id: '01970000-0000-7000-8000-000000000101',
    username: 'ana',
    displayName: 'Ana Muster',
    email: null,
    isAdmin: false,
    rights: { Projects: 'Create', Mcp: 'View' },
    groupIds: ['01970000-0000-7000-8000-000000000001'],
    personId: '01970000-0000-7000-8000-000000000201',
    isTotpEnabled: false,
    hasPassword: true,
    hasMicrosoftAccount: false,
  },
  flags: { mustChangePassword: false, totpSetupRequired: false, totpSetupSuggested: false },
  session: { totpDue: false, signedInUntil: null, authMethod: 'Password' },
}

function answer(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  })
}

describe('the session (docs/03 §4.5, §6.1)', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })
  afterEach(() => vi.unstubAllGlobals())

  it('loads /auth/me and the catalogue, and answers can() and allows() from them', async () => {
    const fetch = vi.fn(async (request: Request) =>
      new URL(request.url).pathname === '/api/v1/auth/me' ? answer(200, me) : answer(200, catalogue),
    )
    vi.stubGlobal('fetch', fetch)
    const session = useSession()
    await session.load()

    expect(session.user?.username).toBe('ana')
    expect(session.personId).toBe(me.user.personId)
    expect(session.groupIds).toEqual(me.user.groupIds)
    expect(session.can('Mcp', 'Use')).toBe(true)
    expect(session.can('Mcp', 'Manage')).toBe(false)
    expect(session.allows(['Projects', 'Create'])).toBe(true)
    expect(session.allows('admin')).toBe(false)
    expect(JSON.parse(localStorage.getItem(sessionCacheKey)!).me.user.id).toBe(me.user.id)
  })

  it('opens offline from the last /auth/me and keeps it while the server is unreachable', async () => {
    localStorage.setItem(sessionCacheKey, JSON.stringify({ me, catalogue }))
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Promise.reject(new TypeError('offline'))),
    )
    setActivePinia(createPinia())
    const session = useSession()

    await session.load()

    expect(session.user?.username).toBe('ana')
    expect(session.can('Mcp', 'Use')).toBe(true)
    await vi.waitFor(() => expect(session.offline).toBe(true))
    expect(session.user).not.toBeNull()
  })

  it('ends on a 401 and forgets the offline copy', async () => {
    localStorage.setItem(sessionCacheKey, JSON.stringify({ me, catalogue }))
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => answer(401, { code: 'unauthorized' })),
    )
    setActivePinia(createPinia())
    const session = useSession()

    await session.load(true)

    expect(session.user).toBeNull()
    expect(localStorage.getItem(sessionCacheKey)).toBeNull()
  })
})
