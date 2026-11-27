import { expect, test } from '@playwright/test'
import { adminApi, problems, pullAll, signInAsAdmin, type Api, type WireRow } from './helpers'

/**
 * *Administration → Statuses* (docs/01 §5, docs/05 §3 phase 3): an admin creates an idea status, moves it, makes it the
 * default and back, archives it – through the statuses endpoints; every change comes back through the sync.
 *
 * The test leaves the seeded statuses as it found them, whatever happens in it (afterEach): their order, the default,
 * and its own status archived – so another spec's package re-import (funnel.spec.ts) finds nothing changed, in any order.
 */
type StatusWire = WireRow & {
  itemType: string
  name: string
  sortOrder: number
  isDefault: boolean
  isArchived: boolean
}

let admin: Api
let before: StatusWire[] = []
let created: string | null = null

async function ideaStatuses(): Promise<StatusWire[]> {
  const rows = ((await pullAll(admin)).statuses ?? []) as StatusWire[]
  return rows.filter((s) => !s.deleted && s.itemType === 'idea').sort((a, b) => a.sortOrder - b.sortOrder)
}

test.beforeEach(async ({ baseURL }) => {
  admin = await adminApi(baseURL)
  before = await ideaStatuses()
  created = null
})

test.afterEach(async () => {
  try {
    const now = await ideaStatuses()
    const formerDefault = before.find((s) => s.isDefault)
    const current = now.find((s) => s.isDefault)
    if (formerDefault && current?.id !== formerDefault.id) {
      const { id, itemType, name, color, icon, category, maxDays } = formerDefault
      await admin.call('PUT', `/api/v1/config/statuses/${id}`, {
        itemType,
        name,
        color,
        icon,
        category,
        maxDays,
        isDefault: true,
      })
    }
    const mine = created ? now.find((s) => s.id === created) : undefined
    if (mine && !mine.isArchived) await admin.call('POST', `/api/v1/config/statuses/${mine.id}/archive`)
    // The order as before, the statuses made meanwhile after it.
    const known = new Set(before.map((s) => s.id))
    const ids = [...before.map((s) => s.id), ...now.filter((s) => !known.has(s.id)).map((s) => s.id)]
    await admin.call('PUT', '/api/v1/config/statuses/order', { itemType: 'idea', ids })
    const restored = await ideaStatuses()
    for (const s of before) {
      expect(restored.find((r) => r.id === s.id)).toMatchObject({ sortOrder: s.sortOrder, isDefault: s.isDefault })
    }
  } finally {
    await admin.dispose()
  }
})

test('statuses: new, reorder, default, archive – back through the sync', async ({ page }) => {
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
  created = (await ideaStatuses()).find((s) => s.name === name)?.id ?? null
  await expect(row).toContainText('max. 5 days')

  // Last in the list: one up.
  await page.getByRole('button', { name: `Move ${name} up` }).click()
  await expect(page.getByText('Order saved')).toBeVisible()
  await expect(page.getByRole('button', { name: `Move ${name} down` })).toBeEnabled()

  // The default: this one, then New again (the seeded default); the default can't be archived.
  const row2 = page.locator('li', { hasText: name })
  await page.getByRole('button', { name: `More actions for ${name}` }).click()
  await page.getByRole('menuitem', { name: 'Set as default' }).click()
  await expect(row2).toContainText('Default')
  const more = page.getByRole('button', { name: `More actions for ${name}` })
  await more.click()
  await expect(page.getByRole('menuitem', { name: 'Archive' })).toHaveAttribute('aria-disabled', 'true')
  await more.click()
  await expect(page.getByRole('menuitem', { name: 'Archive' })).toHaveCount(0)
  const seeded = page.locator('li').filter({ has: page.getByText('New', { exact: true }) })
  await page.getByRole('button', { name: 'More actions for New', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Set as default' }).click()
  await expect(seeded).toContainText('Default')
  await expect(row2).not.toContainText('Default')
  // A click elsewhere closes whatever menu stayed open.
  await page.getByRole('heading', { level: 1, name: 'Statuses' }).click()
  await expect(page.getByRole('menuitem')).toHaveCount(0)

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
