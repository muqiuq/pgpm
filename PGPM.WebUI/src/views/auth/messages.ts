import { ApiError } from '@/api/client'

/** What a failed sign-in step tells the user (PNMTD): never which part was wrong. */
export function signInMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Try again.'
  }
  const minutes = error.retryAfter ? Math.max(1, Math.ceil(error.retryAfter / 60)) : null
  switch (error.code) {
    case 'invalid_credentials':
      return 'Wrong username or password.'
    case 'invalid_code':
      return 'Wrong code, or one that was used already. Wait for the next one and try again.'
    case 'locked_out':
      return `Too many failed sign-ins. Try again in ${minutes ?? 'a few'} minute${minutes === 1 ? '' : 's'}.`
    case 'rate_limited':
      return 'Too many attempts from this address. Wait a minute and try again.'
    case 'login_expired':
      return 'The sign-in has expired. Enter your password again.'
    case 'local_login_not_allowed':
      return 'Signing in with a password is not allowed from this network. Sign in with Microsoft.'
    case 'network':
      return 'The server cannot be reached.'
    default:
      return error.message
  }
}

/** The reasons a Microsoft sign-in comes back with (`/sign-in?ssoError=…`, GGITSV). */
export const ssoErrors: Record<string, string> = {
  failed: 'Signing in with Microsoft failed. Please try again.',
  cancelled: 'Signing in with Microsoft was cancelled.',
  deactivated: 'Your PGPM account has been deactivated. Please contact an administrator.',
  'account-conflict':
    'Your username already belongs to another PGPM account. An administrator can link your Microsoft account to it.',
  'no-account': 'There is no PGPM account for your Microsoft account. Please ask an administrator.',
}
