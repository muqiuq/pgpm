import { expect, test } from '@playwright/test'
import { problems, signInAsAdmin } from './helpers'

/**
 * *Administration → Statuses* (docs/01 §5, docs/05 §3 phase 3): an admin creates an idea status, moves it, makes it the
 * default and back, archives it – through the statuses endpoints; every change comes back through the sync.
 * fixme until the server's `/api/v1/config/statuses` endpoints are merged (the integration step enables it).
 */
test.fixme('statuses: new, reorder, default, archive – back through the sync', async ({ page }) => {
  const seen = problems(page)
  const name = `E2E status ${Date.now().toString(36)}`
  await signInAsAdmin(page)
  await page.goto('/admin/statuses')
  await expect(page.getByRole('heading', { level: 1, name: 'Statuses' })).toBeVisible()
  await expect(page.getByRole('radio', { name: 'Ideas' }).or(page.getByRole('button', { name: 'Ideas' }))).toBeVisible()

  await page.getByRole('button', { name: 'New status' }).click()
  const dialog = page.getByRole('dialog', { name: 'New idea status' })
  await dialog.getByLabel('Name').fill(name)
  await dialog.getByRole('button', { name: 'Green' }).click()
  await dialog.getByLabel('Icon').fill('pi-eye')
  await dialog.getByLabel('Max. days').fill('5')
  await dialog.getByRole('button', { name: 'Create status' }).click()
  const row = page.locator('li', { hasText: name })
  await expect(row).toBeVisible()
  await expect(row).toContainText('max. 5 days')

  // Last in the list: one up.
  await page.getByRole('button', { name: `Move ${name} up` }).click()
  await expect(page.getByText('Order saved')).toBeVisible()
  await expect(page.getByRole('button', { name: `Move ${name} down` })).toBeEnabled()

  await page.getByRole('button', { name: `More actions for ${name}` }).click()
  await page.getByRole('menuitem', { name: 'Archive' }).click()
  await expect(page.locator('li', { hasText: name })).toHaveCount(0)
  await page.getByText(/Show archived/).click()
  await expect(page.locator('li', { hasText: name })).toContainText('Archived')

  await page.getByRole('button', { name: `More actions for ${name}` }).click()
  await page.getByRole('menuitem', { name: 'Unarchive' }).click()
  await expect(page.locator('li', { hasText: name })).not.toContainText('Archived')
  expect(seen).toEqual([])
})
