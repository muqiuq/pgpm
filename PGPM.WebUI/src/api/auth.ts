import { call, client, type Schemas } from './client'

export type MeResponse = Schemas['MeResponse']
export type CurrentUser = Schemas['CurrentUser']
export type SessionFlags = Schemas['SessionFlags']
export type SessionTimes = Schemas['SessionTimes']
export type LoginResponse = Schemas['LoginResponse']
export type SignInOptions = Schemas['SignInOptions']
export type TotpSetup = Schemas['TotpSetup']
export type AccessItem = Schemas['AccessItem']
export type RightsAreaItem = Schemas['RightsAreaItem']

/** Sign-in, the session and the own account (PNMTD, docs/03 §4.5), with GGITSV's Microsoft sign-in (§5.4). */
export const authApi = {
  me: () => call(client.GET('/api/v1/auth/me')),
  options: () => call(client.GET('/api/v1/auth/options')),
  login: (username: string, password: string) =>
    call(client.POST('/api/v1/auth/login', { body: { username, password } })),
  loginTotp: (code: string) => call(client.POST('/api/v1/auth/login/totp', { body: { code } })),
  /** The day's 2FA code: the session goes on. */
  totpDaily: (code: string) => call(client.POST('/api/v1/auth/totp/daily', { body: { code } })),
  logout: () => call(client.POST('/api/v1/auth/logout')),
  /** The rights catalogue (docs/03 §6.2): areas, their levels in order, what each allows. */
  rights: () => call(client.GET('/api/v1/rights')),
  access: () => call(client.GET('/api/v1/account/access')),
  changePassword: (currentPassword: string, newPassword: string) =>
    call(client.POST('/api/v1/account/password', { body: { currentPassword, newPassword } })),
  revokeSessions: () => call(client.POST('/api/v1/account/sessions/revoke')),
  totpSetup: () => call(client.POST('/api/v1/account/totp/setup')),
  totpConfirm: (code: string) => call(client.POST('/api/v1/account/totp/confirm', { body: { code } })),
  totpDisable: (password: string, code: string) =>
    call(client.POST('/api/v1/account/totp/disable', { body: { password, code } })),
}

/**
 * `GET /api/v1/auth/sso/microsoft` (GGITSV): the browser navigates there – a full navigation, not a fetch – and comes
 * back to `returnTo` (a path of this app) or to `/sign-in?ssoError=…`.
 */
export function microsoftSignInUrl(returnTo?: string | null): string {
  const query = returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''
  return `/api/v1/auth/sso/microsoft${query}`
}
