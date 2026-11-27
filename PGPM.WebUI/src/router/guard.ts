import type { Right } from '@/api/rights'

/** What the guard needs to know about the session. */
export interface GuardState {
  /** Phase 1: a session from /auth/me (or its offline copy). Phase 0 has no sign-in. */
  signedIn: boolean
  allows: (right: Right) => boolean
}

/** What the guard needs to know about the target route. */
export interface GuardTarget {
  name: string | symbol | null | undefined
  fullPath: string
  /** No sign-in needed (phase 1: the sign-in screens). */
  public?: boolean
  /** What the route needs (docs/03 §6.1). */
  right?: Right
}

export type GuardResult = true | { name: string; query?: Record<string, string> }

/** Where a user without the right for a page lands: the first entry of the menu. */
export const homeRoute = 'maps'

/**
 * A return URL that stays inside the app (PNMTD): a path of the router, never another origin ("//evil", "/\\evil",
 * "https://…"), and never a sign-in screen.
 */
export function safeReturnUrl(value: unknown): string | null {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return null
  }
  if (/^\/(login|change-password|account\/setup-2fa)(\/|\?|$)/.test(value)) {
    return null
  }
  return value
}

/**
 * Where a navigation may go. The server protects itself (deny by default); this only spares the user dead ends.
 *
 * Phase 1 hook – PNMTD's decide() adds, before the right check and in this order: signed out → `login` with
 * `{ returnUrl }` (safeReturnUrl); a password to change → `change-password`; 2FA required and not set up → `setup-2fa`;
 * and a signed-in user on `login` → home. The state then comes from the session store's /auth/me.
 */
export function decide(to: GuardTarget, state: GuardState): GuardResult {
  if (to.public) {
    return true
  }
  if (to.right && !state.allows(to.right)) {
    return to.name === homeRoute ? true : { name: homeRoute }
  }
  return true
}
