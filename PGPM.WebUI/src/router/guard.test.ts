import { describe, expect, it } from 'vitest'
import type { SessionFlags } from '@/api/auth'
import { decide, homeRoute, nextAfterSignIn, safeReturnUrl, type GuardState } from './guard'

describe('the guard (PNMTD order)', () => {
  const none: SessionFlags = { mustChangePassword: false, totpSetupRequired: false, totpSetupSuggested: false }
  const signedIn = (flags: Partial<SessionFlags> = {}, allows = true): GuardState => ({
    signedIn: true,
    flags: { ...none, ...flags },
    allows: () => allows,
  })
  const signedOut: GuardState = { signedIn: false, flags: null, allows: () => false }

  it('sends a signed-out user to the sign-in with the page to come back to', () => {
    expect(decide({ name: 'projects', fullPath: '/projects?tab=open' }, signedOut)).toEqual({
      name: 'sign-in',
      query: { returnUrl: '/projects?tab=open' },
    })
    expect(decide({ name: 'sign-in', fullPath: '/sign-in', public: true }, signedOut)).toBe(true)
  })

  it('sends a signed-in user from the sign-in home, and keeps the other public pages open', () => {
    expect(decide({ name: 'sign-in', fullPath: '/sign-in', public: true }, signedIn())).toEqual({ name: homeRoute })
    expect(decide({ name: 'sign-in-2fa', fullPath: '/sign-in/2fa', public: true }, signedIn())).toBe(true)
  })

  it('asks for a new password first, then for a required 2FA setup', () => {
    const both = signedIn({ mustChangePassword: true, totpSetupRequired: true })
    expect(decide({ name: 'users', fullPath: '/admin/users', right: 'admin' }, both)).toEqual({
      name: 'change-password',
      query: { returnUrl: '/admin/users' },
    })
    expect(decide({ name: 'change-password', fullPath: '/change-password' }, both)).toBe(true)
    const totp = signedIn({ totpSetupRequired: true })
    expect(decide({ name: 'maps', fullPath: '/maps' }, totp)).toEqual({
      name: 'setup-2fa',
      query: { returnUrl: '/maps' },
    })
    expect(decide({ name: 'setup-2fa', fullPath: '/account/setup-2fa' }, totp)).toBe(true)
    expect(decide({ name: 'change-password', fullPath: '/change-password' }, signedIn())).toEqual({ name: 'account' })
  })

  it('sends a user without the right for a page home, and never loops there', () => {
    const without = signedIn({}, false)
    expect(decide({ name: 'users', fullPath: '/admin/users', right: 'admin' }, without)).toEqual({ name: homeRoute })
    expect(decide({ name: homeRoute, fullPath: '/maps', right: ['Maps', 'Create'] }, without)).toBe(true)
    expect(decide({ name: 'projects', fullPath: '/projects' }, without)).toBe(true)
  })

  it('goes on after a sign-in: password, required setup, the suggestion, then where the user wanted to go', () => {
    expect(nextAfterSignIn({ ...none, mustChangePassword: true }, '/notes')).toEqual({
      name: 'change-password',
      query: { returnUrl: '/notes' },
    })
    expect(nextAfterSignIn({ ...none, totpSetupRequired: true }, null)).toEqual({ name: 'setup-2fa', query: {} })
    expect(nextAfterSignIn({ ...none, totpSetupSuggested: true }, '/notes')).toEqual({
      name: 'setup-2fa',
      query: { returnUrl: '/notes', prompt: '1' },
    })
    expect(nextAfterSignIn(none, '/notes')).toBe('/notes')
    expect(nextAfterSignIn(none, null)).toBe('/')
  })

  it('keeps return URLs inside the app and off the sign-in screens', () => {
    expect(safeReturnUrl('/projects/42?tab=history')).toBe('/projects/42?tab=history')
    for (const bad of [
      '//evil.example',
      '/\\evil.example',
      'https://evil.example',
      '/sign-in?x=1',
      '/change-password',
      42,
      undefined,
    ]) {
      expect(safeReturnUrl(bad), String(bad)).toBeNull()
    }
  })
})
