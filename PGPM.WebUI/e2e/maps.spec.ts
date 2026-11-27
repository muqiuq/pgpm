import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test'
import { createTeam, signInAs, type Team, type TestUser } from './helpers'

/**
 * Maps in two browsers (docs/05 §3 phase 5 "done when", doc 01 §12): each browser context a device of a team user
 * (Maps: Create, Ideas: Create), against the e2e stack. fixme until the phase 5 client is merged with the maps'
 * server side and a stack with the seeded `innovation-ai` package (the Prioritization matrix, the assessment fields)
 * is available (the integration step enables them). The same flows run in Vitest against the fake server
 * (src/maps/localSource.sync.test.ts, src/views/maps/MapView.test.ts).
 */

let team: Team
const contexts: BrowserContext[] = []

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL, ['Ana', 'Ben'], { Maps: 'Create', Ideas: 'Create', Notes: 'Create' })
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await team.admin.dispose()
})

async function device(browser: Browser, user: TestUser, path = '/maps'): Promise<Page> {
  const context = await browser.newContext()
  contexts.push(context)
  const page = await context.newPage()
  await signInAs(page, user, path)
  await expect(page.getByTestId('sync-indicator')).toContainText('Live')
  return page
}

/** New canvas map, shared with the team at edit (preselected); answers its id. */
async function createCanvas(page: Page, name: string): Promise<string> {
  await page.goto('/maps')
  await page.getByRole('button', { name: 'New map' }).click()
  await page.getByRole('menuitem', { name: 'Canvas map' }).click()
  const dialog = page.getByRole('dialog', { name: 'New canvas map' })
  await dialog.getByLabel('Name').fill(name)
  await expect(dialog.locator('li', { hasText: team.group.name }).getByRole('combobox')).toContainText('Edit')
  await dialog.getByRole('button', { name: 'Create map' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  const id = /\/maps\/([0-9a-f-]{36})/.exec(page.url())?.[1]
  expect(id).toBeTruthy()
  return id!
}

/** A rectangle drawn with the toolbar where the user drags. */
async function drawRectangle(page: Page) {
  await page.getByRole('button', { name: 'Rectangle' }).click()
  const svg = page.getByTestId('map-canvas').locator('svg.surface')
  const box = (await svg.boundingBox())!
  await page.mouse.move(box.x + 200, box.y + 200)
  await page.mouse.down()
  await page.mouse.move(box.x + 320, box.y + 280, { steps: 4 })
  await page.mouse.up()
}

test.fixme('two users co-edit a map live', async ({ browser }) => {
  const [ana, ben] = await Promise.all([device(browser, team.users[0]!), device(browser, team.users[1]!)])
  const id = await createCanvas(ana, 'Co-editing')
  await ben.goto(`/maps/${id}`)
  await expect(ben.getByRole('heading', { level: 1, name: 'Co-editing' })).toBeVisible()
  await drawRectangle(ana)
  const rect = ben.locator('g.map-element.shape')
  const started = Date.now()
  await expect(rect).toHaveCount(1)
  expect(Date.now() - started).toBeLessThan(2000)
  // Ben moves it; Ana sees it move.
  const before = await ana.locator('g.map-element.shape').getAttribute('transform')
  const box = (await rect.boundingBox())!
  await ben.mouse.move(box.x + 20, box.y + 20)
  await ben.mouse.down()
  await ben.mouse.move(box.x + 140, box.y + 90, { steps: 5 })
  await ben.mouse.up()
  await expect(ana.locator('g.map-element.shape')).not.toHaveAttribute('transform', before ?? '')
})

test.fixme('offline map editing syncs on reconnect, a collision is a conflict of that map', async ({ browser }) => {
  const [ana, ben] = await Promise.all([device(browser, team.users[0]!), device(browser, team.users[1]!)])
  const id = await createCanvas(ana, 'Offline map')
  await drawRectangle(ana)
  await ben.goto(`/maps/${id}`)
  await expect(ben.locator('g.map-element.shape')).toHaveCount(1)
  await ben.context().setOffline(true)
  await expect(ben.getByTestId('sync-indicator')).toContainText('Offline')
  // Ben adds a text and recolours the rectangle offline; Ana recolours it too.
  await ben.getByRole('button', { name: 'Text' }).click()
  await ben
    .getByTestId('map-canvas')
    .locator('svg.surface')
    .click({ position: { x: 400, y: 120 } })
  await ben.keyboard.type('Written offline')
  await ben.keyboard.press('Escape')
  for (const page of [ben, ana]) {
    await page.locator('g.map-element.shape').click()
    await page.getByRole('button', { name: 'Fill' }).click()
    await page.getByRole('button', { name: page === ben ? 'Pink' : 'Green' }).click()
  }
  await ben.context().setOffline(false)
  await expect(ana.getByText('Written offline')).toBeVisible()
  await expect(ben.getByText('1 conflict')).toBeVisible()
  await ben.getByText('1 conflict').click()
  await expect(ben.getByRole('dialog')).toContainText('Offline map')
})

test.fixme('the seeded matrix map shows assessed ideas in quadrants; a card moves when a rating changes', async ({
  browser,
}) => {
  const ana = await device(browser, team.users[0]!)
  await ana.goto('/maps')
  await ana.getByRole('button', { name: 'New map' }).click()
  await ana.getByRole('menuitem', { name: 'Matrix map' }).click()
  const dialog = ana.getByRole('dialog', { name: 'New matrix map' })
  await expect(dialog).toContainText('Prioritization matrix')
  await dialog.getByLabel('Name').fill('Review board')
  await dialog.getByRole('button', { name: 'Create map' }).click()
  await expect(ana.getByRole('heading', { level: 1, name: 'Review board' })).toBeVisible()
  // The seeded, assessed ideas sit in their quadrants; the others wait in the tray.
  await expect(ana.locator('[data-element-id^="matrix:"]').first()).toBeVisible()
  await expect(ana.getByRole('complementary', { name: 'Not yet assessed' })).toBeVisible()
  const card = ana.locator('[data-element-id^="matrix:"]').first()
  const before = await card.getAttribute('style')
  await card.click()
  await ana.getByTestId('item-quick-edit').getByRole('group').first().getByRole('button', { name: '0' }).click()
  await expect(card).not.toHaveAttribute('style', before ?? '')
})

test.fixme('a map that is no longer shared disappears from the other device', async ({ browser }) => {
  const [ana, ben] = await Promise.all([device(browser, team.users[0]!), device(browser, team.users[1]!)])
  const id = await createCanvas(ana, 'Soon private')
  await ben.goto('/maps')
  await expect(ben.getByRole('link', { name: 'Soon private' })).toBeVisible()
  await ana.getByRole('button', { name: /^Share/ }).click()
  const share = ana.getByTestId('share-dialog')
  await share.locator('li', { hasText: team.group.name }).getByRole('combobox').click()
  await ana.getByRole('option', { name: 'No access', exact: true }).click()
  await share.getByRole('button', { name: 'Save' }).click()
  await expect(ben.getByRole('link', { name: 'Soon private' })).toHaveCount(0)
  await ben.goto(`/maps/${id}`)
  await expect(ben.getByText('This map is not on this device.')).toBeVisible()
})
