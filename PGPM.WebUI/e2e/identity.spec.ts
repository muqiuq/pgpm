import { expect, test } from '@playwright/test'
import { adminPassword, changePassword, initialAdminPassword, problems, signIn, signOut } from './helpers'

/**
 * Identity and rights (docs/05 §3 phase 1): the first admin signs in with the initial password and must change it; an
 * admin creates a group and a user; that user signs in, changes the temporary password and sees no administration.
 * Runs first (file order): the stack is fresh, the admin's password still the initial one.
 */
test('the first admin, a new group and user, and what that user sees', async ({ page }) => {
  test.skip(!initialAdminPassword, 'E2E_ADMIN_PASSWORD is not set (tools/e2e.sh sets it)')
  const seen = problems(page)
  const stamp = Date.now().toString(36)

  // Signed out, any page leads to the sign-in, which comes back to it.
  await page.goto('/')
  await expect(page).toHaveURL(/\/sign-in\?returnUrl=\/maps$/)
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Sign in with Microsoft' })).toHaveCount(0)
  await signIn(page, 'admin', 'not-the-password')
  await expect(page.getByRole('alert')).toHaveText('Wrong username or password.')

  await signIn(page, 'admin', initialAdminPassword)
  await expect(page).toHaveURL(/\/change-password/)
  await changePassword(page, initialAdminPassword, adminPassword)
  await expect(page).toHaveURL(/\/maps$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Maps' })).toBeVisible()

  // A group with a right …
  const group = `E2E team ${stamp}`
  await page.getByRole('button', { name: 'Administration' }).click()
  await page.getByRole('link', { name: 'Groups & Rights', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Groups & Rights' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Members', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'New group' }).click()
  await page.getByLabel('Name').fill(group)
  await page.getByRole('button', { name: 'Create group' }).click()
  await expect(page.getByRole('heading', { level: 1, name: group })).toBeVisible()
  await page.getByRole('group', { name: 'Notes' }).getByRole('button', { name: 'Create' }).click()
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByText(`${group} saved`)).toBeVisible()

  // … and a user, with a temporary password shown once.
  const username = `e2e-${stamp}`
  await page.getByRole('link', { name: 'Users', exact: true }).click()
  await page.getByRole('button', { name: 'New user' }).click()
  await page.getByLabel('Username', { exact: true }).fill(username)
  await page.getByLabel('Name', { exact: true }).fill('E2E Person')
  await page.getByRole('button', { name: 'Create user' }).click()
  const temporary = (await page.getByTestId('copy-value').textContent())!.trim()
  expect(temporary).toHaveLength(20)
  await page.getByRole('button', { name: 'Done' }).click()
  await expect(page.getByRole('cell', { name: new RegExp(username) })).toBeVisible()

  // The user joins the group on its page.
  await page.getByRole('link', { name: 'Groups & Rights', exact: true }).click()
  await page.getByRole('link', { name: group }).click()
  await page.getByRole('combobox', { name: 'User to add' }).click()
  await page.getByRole('option', { name: new RegExp(username) }).click()
  await page.getByRole('button', { name: 'Add', exact: true }).click()
  await expect(page.getByText(`${username} (E2E Person) added`)).toBeVisible()

  // The audit log has it all.
  await page.getByRole('link', { name: 'Audit log', exact: true }).click()
  await expect(page.getByText('User: created').first()).toBeVisible()
  await signOut(page)

  // The new user: the temporary password, a new one, and no administration.
  await signIn(page, username, temporary)
  await changePassword(page, temporary, 'e2e-user-password-1')
  await expect(page).toHaveURL(/\/maps$/)
  const nav = page.locator('nav.drawer-nav')
  await expect(nav.getByRole('link', { name: 'Notes', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Administration' })).toHaveCount(0)
  await expect(nav.getByRole('link', { name: 'Users', exact: true })).toHaveCount(0)
  await page.goto('/admin/users')
  await expect(page).toHaveURL(/\/maps$/)
  await page.getByTestId('user-menu').click()
  await expect(page.getByRole('menuitem', { name: 'Account' })).toBeVisible()
  await page.getByRole('menuitem', { name: 'Security' }).click()
  await expect(page.getByText(`Your groups: ${group}.`)).toBeVisible()
  await signOut(page)
  await page.goto('/notes')
  await expect(page).toHaveURL(/\/sign-in\?returnUrl=\/notes$/)

  expect(seen).toEqual([])
})
