import { describe, expect, it } from 'vitest'
import { decide, homeRoute, safeReturnUrl } from './guard'

describe('the guard', () => {
  const everything = { signedIn: false, allows: () => true }
  const nothing = { signedIn: true, allows: () => false }

  it('lets every page open while every right is allowed (phase 0: no sign-in yet)', () => {
    expect(decide({ name: 'projects', fullPath: '/projects' }, everything)).toBe(true)
    expect(decide({ name: 'users', fullPath: '/admin/users', right: 'admin' }, everything)).toBe(true)
  })

  it('sends a user without the right for a page to the start, and never loops there', () => {
    expect(decide({ name: 'users', fullPath: '/admin/users', right: 'admin' }, nothing)).toEqual({ name: homeRoute })
    expect(decide({ name: homeRoute, fullPath: '/maps', right: ['Maps', 'Create'] }, nothing)).toBe(true)
    expect(decide({ name: 'users', fullPath: '/admin/users', right: 'admin', public: true }, nothing)).toBe(true)
  })

  it('keeps return URLs inside the app', () => {
    expect(safeReturnUrl('/projects/42?tab=history')).toBe('/projects/42?tab=history')
    for (const bad of ['//evil.example', '/\\evil.example', 'https://evil.example', '/login?x=1', 42, undefined]) {
      expect(safeReturnUrl(bad), String(bad)).toBeNull()
    }
  })
})
