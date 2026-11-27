import type { SessionFlags } from '@/api/auth'
import type { Right } from '@/api/rights'

/** What the guard needs to know about the session. */
export interface GuardState {
  /** A session from /auth/me (or its offline copy). */
  signedIn: boolean
  flags: SessionFlags | null
  allows: (right: Right) => boolean
}

/** What the guard needs to know about the target route. */
export interface GuardTarget {
  name: string | symbol | null | undefined
  fullPath: string
  /** No sign-in needed (the sign-in screens). */
  public?: boolean
  /** What the route needs (docs/03 §6.1). */
  right?: Right
}

export type GuardResult = true | { name: string; query?: Record<string, string> }

/** Where a user without the right for a page lands, and where a sign-in ends: the first entry of the menu. */
export const homeRoute = 'maps'

/**
 * A return URL that stays inside the app (PNMTD): a path of the router, never another origin ("//evil", "/\\evil",
 * "https://…"), and never a sign-in screen.
 */
export function safeReturnUrl(value: unknown): string | null {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return null
  }
  if (/^\/(sign-in|change-password|account\/setup-2fa)(\/|\?|$)/.test(value)) {
    return null
  }
  return value
}

/**
 * Where a navigation may go (PNMTD's order). The server protects itself (deny by default, the session gates); this only
 * spares the user dead ends:
 * signed out → `sign-in` with `{ returnUrl }`; a password to change → `change-password` first; 2FA required and not set
 * up → `setup-2fa`; a right it lacks → home. The day's 2FA code is no navigation: the page opens and the dialog asks
 * over it (router/index.ts, sessionHandling.ts). A signed-in user on the sign-in page goes home.
 */
export function decide(to: GuardTarget, state: GuardState): GuardResult {
  const returnUrl: Record<string, string> = safeReturnUrl(to.fullPath) ? { returnUrl: to.fullPath } : {}
  if (to.public) {
    return state.signedIn && to.name === 'sign-in' ? { name: homeRoute } : true
  }
  if (!state.signedIn) {
    return { name: 'sign-in', query: returnUrl }
  }
  if (state.flags?.mustChangePassword) {
    return to.name === 'change-password' ? true : { name: 'change-password', query: returnUrl }
  }
  if (to.name === 'change-password') {
    return { name: 'account' }
  }
  if (state.flags?.totpSetupRequired) {
    return to.name === 'setup-2fa' ? true : { name: 'setup-2fa', query: returnUrl }
  }
  if (to.right && !state.allows(to.right)) {
    return to.name === homeRoute ? true : { name: homeRoute }
  }
  return true
}

/**
 * After a complete sign-in or a forced password change (PNMTD): the password first, then a required 2FA setup, then
 * the prompt that suggests it (once per sign-in), then where the user wanted to go.
 */
export function nextAfterSignIn(
  flags: SessionFlags,
  returnUrl: string | null,
  prompt = true,
): { name: string; query: Record<string, string> } | string {
  const query: Record<string, string> = returnUrl ? { returnUrl } : {}
  if (flags.mustChangePassword) {
    return { name: 'change-password', query }
  }
  if (flags.totpSetupRequired) {
    return { name: 'setup-2fa', query }
  }
  if (flags.totpSetupSuggested && prompt) {
    return { name: 'setup-2fa', query: { ...query, prompt: '1' } }
  }
  return returnUrl ?? '/'
}
