import { expect, type Page } from '@playwright/test'

/** The first admin of the e2e stack (tools/e2e.sh passes Auth__InitialAdmin__Password) … */
export const initialAdminPassword = process.env.E2E_ADMIN_PASSWORD ?? ''

/** … and the password the first test changes it to; later tests sign in with it. */
export const adminPassword = 'e2e-admin-password-1'

/**
 * Script errors, CSP violations and Vue's warnings while a test runs: collected from the start, asserted at the end.
 * Expected failures don't count: a signed-out start's 401 of /auth/me, the gates' 403 before the password change, and
 * the offline test's unreachable server.
 */
export function problems(page: Page): string[] {
  const seen: string[] = []
  page.on('pageerror', (error) => seen.push(`pageerror: ${error.message}`))
  page.on('console', (message) => {
    const text = message.text()
    if (message.type() === 'error' || /\[Vue warn\]|Content Security Policy/.test(text)) {
      if (/Failed to load resource: net::ERR_INTERNET_DISCONNECTED/.test(text)) return
      // The live channel's reconnect attempts while offline (the sync falls back to polling).
      if (/WebSocket connection to '.*\/api\/v1\/sync\/live' failed/.test(text)) return
      if (/Failed to load resource: the server responded with a status of (401|403)/.test(text)) return
      seen.push(`${message.type()}: ${text}`)
    }
  })
  return seen
}

/** The local sign-in form, filled and sent. */
export async function signIn(page: Page, username: string, password: string) {
  if (!/\/sign-in/.test(page.url())) await page.goto('/sign-in')
  await page.getByLabel('Username', { exact: true }).fill(username)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
}

/** The forced password change, and the 2FA suggestion after it put off. */
export async function changePassword(page: Page, current: string, next: string) {
  await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible()
  await page.getByLabel('Password you signed in with').fill(current)
  await page.getByLabel('New password', { exact: true }).fill(next)
  await page.getByLabel('New password again').fill(next)
  await page.getByRole('button', { name: 'Save and continue' }).click()
  await expect(page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })).toBeVisible()
  await page.getByRole('button', { name: 'Later' }).click()
}

/** Signed in as the admin, whichever test ran first; the 2FA suggestion put off. */
export async function signInAsAdmin(page: Page) {
  await page.goto('/sign-in')
  await signIn(page, 'admin', adminPassword)
  const suggestion = page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })
  const failed = page.getByRole('alert')
  await expect(suggestion.or(failed)).toBeVisible()
  if (await failed.isVisible()) {
    // The first test of a fresh stack: the initial password, changed now.
    await signIn(page, 'admin', initialAdminPassword)
    await changePassword(page, initialAdminPassword, adminPassword)
  } else {
    await page.getByRole('button', { name: 'Later' }).click()
  }
  await expect(page.getByTestId('user-menu')).toBeVisible()
}

export async function signOut(page: Page) {
  await page.getByTestId('user-menu').click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
}
