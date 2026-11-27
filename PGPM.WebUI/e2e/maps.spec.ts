import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test'
import {
  Api,
  createTeam,
  localRows,
  pullAll,
  pushInserts,
  signInAs,
  uuidv7,
  type Team,
  type TestUser,
  type WireRow,
} from './helpers'

/**
 * Maps in two browsers (docs/05 §3 phase 5 "done when", doc 01 §12): each browser context a device of a team user
 * (Maps: Create, Ideas: Create), against the e2e stack with the seeded `core` and `innovation-ai` packages (the
 * Prioritization matrix, the assessment fields). The same flows run in Vitest against the fake server
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

/**
 * The time from the end of a gesture on one page (its pointerup) to a change on the other – the first element matching
 * `selector` there, or its `transform` changing – in ms, both read from the browser's clock (the contexts share one
 * browser process), the change watched by a MutationObserver.
 */
async function liveLatency(from: Page, to: Page, selector: string, gesture: () => Promise<void>): Promise<number> {
  await to.evaluate((selector) => {
    const w = window as unknown as { pgpmSeen?: Promise<number> }
    const before = document.querySelector(selector)?.getAttribute('transform') ?? null
    const changed = () => {
      const el = document.querySelector(selector)
      return !!el && (before === null || el.getAttribute('transform') !== before)
    }
    w.pgpmSeen = new Promise<number>((resolve) => {
      const observer = new MutationObserver(() => {
        if (changed()) {
          observer.disconnect()
          resolve(Date.now())
        }
      })
      observer.observe(document.body, { subtree: true, childList: true, attributes: true })
    })
  }, selector)
  await from.evaluate(() =>
    document.addEventListener('pointerup', () => ((window as unknown as { pgpmUp: number }).pgpmUp = Date.now()), {
      capture: true,
      once: true,
    }),
  )
  await gesture()
  const seenAt = await to.evaluate(() =>
    Promise.race([
      (window as unknown as { pgpmSeen: Promise<number> }).pgpmSeen,
      new Promise<number>((resolve) => setTimeout(() => resolve(Number.NaN), 10_000)),
    ]),
  )
  return seenAt - (await from.evaluate(() => (window as unknown as { pgpmUp: number }).pgpmUp))
}

test('two users co-edit a map live', async ({ browser }) => {
  const [ana, ben] = await Promise.all([device(browser, team.users[0]!), device(browser, team.users[1]!)])
  const id = await createCanvas(ana, 'Co-editing')
  await ben.goto(`/maps/${id}`)
  await expect(ben.getByRole('heading', { level: 1, name: 'Co-editing' })).toBeVisible()
  // Ana draws a rectangle: it appears at Ben.
  const drawn = await liveLatency(ana, ben, 'g.map-element.shape', () => drawRectangle(ana))
  const rect = ben.locator('g.map-element.shape')
  await expect(rect).toHaveCount(1)
  // Ben moves it; Ana sees it move.
  const box = (await rect.boundingBox())!
  const moved = await liveLatency(ben, ana, 'g.map-element.shape', async () => {
    await ben.mouse.move(box.x + 20, box.y + 20)
    await ben.mouse.down()
    await ben.mouse.move(box.x + 140, box.y + 90, { steps: 5 })
    await ben.mouse.up()
  })
  console.log(`map live latency: drawn ${drawn} ms, moved ${moved} ms`)
  test.info().annotations.push({ type: 'map live latency (ms)', description: `${drawn} / ${moved}` })
  expect(drawn).toBeLessThan(1000)
  expect(moved).toBeLessThan(1000)
})

test('offline map editing syncs on reconnect, a collision is a conflict of that map', async ({ browser }) => {
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
  await expect(ben.locator('textarea.inline-editor')).toBeFocused()
  await ben.keyboard.type('Written offline')
  // Ctrl+Enter keeps the text (Escape would drop it).
  await ben.keyboard.press('Control+Enter')
  await expect(ben.getByText('Written offline')).toBeVisible()
  for (const page of [ben, ana]) {
    await page.locator('g.map-element.shape').click()
    await page
      .getByRole('radiogroup', { name: 'Fill' })
      .getByRole('radio', { name: page === ben ? 'pink' : 'green', exact: true })
      .click()
  }
  // Ana's colour is on the server before Ben comes back: his is the late one.
  await expect.poll(async () => (await localRows(ana, 'outbox')).length).toBe(0)
  await ben.context().setOffline(false)
  await expect(ana.getByText('Written offline')).toBeVisible()
  await expect(ben.getByText('1 conflict')).toBeVisible()
  await ben.getByText('1 conflict').click()
  await expect(ben.getByRole('dialog')).toContainText('Offline map')
})

/**
 * Ideas of a user with the playbook's assessment, pushed through the sync as her device would: feasibility – the 12
 * ratings at `feasibility` and the time to proof of value < 3 months (5 points) – and impact – reach and strategic fit
 * at `impact`, the value band and as many strategic advantages. None given: not assessed.
 */
async function assessedIdeas(
  baseURL: string | undefined,
  user: TestUser,
  groupId: string,
  ideas: { title: string; feasibility?: number; impact?: number }[],
): Promise<string[]> {
  const api = await Api.create(baseURL)
  await api.signIn(user.username, user.password)
  const config = await pullAll(api)
  const field = (key: string) => {
    const found = config.field_definitions?.find((f) => f.key === key)
    if (!found) throw new Error(`The seeded field ${key} is missing`)
    return found
  }
  const status = config.statuses!.find((s) => s.itemType === 'idea' && s.isDefault)!
  const feasibility = (field('feasibility_index').config as { inputs: string[] }).inputs
  const bands = ['lt10k', 'k10_50', 'k50_200', 'k200_1m', 'gt1m']
  const advantages = ['customer_experience', 'revenue_growth', 'higher_sales', 'less_complexity', 'efficiency']
  const ids: string[] = []
  for (const idea of ideas) {
    const id = uuidv7()
    ids.push(id)
    const rows: { table: string; row: WireRow }[] = [
      { table: 'items', row: { id, type: 'idea', title: idea.title, statusId: status.id } },
      { table: 'item_shares', row: { id: uuidv7(), itemId: id, groupId, access: 'edit' } },
    ]
    const value = (key: string, v: unknown) =>
      rows.push({ table: 'field_values', row: { id: uuidv7(), itemId: id, fieldId: field(key).id, value: v } })
    if (idea.feasibility !== undefined) {
      for (const key of feasibility) value(key, key === 'time_to_proof_of_value' ? 'lt3' : idea.feasibility)
    }
    if (idea.impact !== undefined) {
      value('impact_reach', idea.impact)
      value('impact_strategic', idea.impact)
      value('impact_value_band', bands[Math.max(0, idea.impact - 1)])
      value('strategic_advantages', advantages.slice(0, idea.impact))
    }
    expect(new Set(await pushInserts(api, rows))).toEqual(new Set(['accepted']))
  }
  await api.dispose()
  return ids
}

/** The quadrant (`q-tr`, …) a card's centre lies in. */
async function quadrantOf(page: Page, itemTitle: string): Promise<string | null> {
  const card = page.getByRole('button', { name: new RegExp(`${itemTitle}$`) }).first()
  const box = (await card.boundingBox())!
  const [x, y] = [box.x + box.width / 2, box.y + box.height / 2]
  for (const q of ['tl', 'tr', 'bl', 'br']) {
    const area = await page.locator(`rect.quadrant.q-${q}`).boundingBox()
    if (area && x >= area.x && x <= area.x + area.width && y >= area.y && y <= area.y + area.height) return `q-${q}`
  }
  return null
}

test('the seeded matrix map shows assessed ideas in quadrants; a card moves when a rating changes', async ({
  browser,
  baseURL,
}) => {
  const stamp = Date.now().toString(36)
  const [star, quick, waiting] = [`Star ${stamp}`, `Quick ${stamp}`, `Waiting ${stamp}`]
  await assessedIdeas(baseURL, team.users[0]!, team.group.id, [
    { title: star, feasibility: 5, impact: 5 },
    { title: quick, feasibility: 5, impact: 1 },
    { title: waiting, feasibility: 3 },
  ])
  const ana = await device(browser, team.users[0]!)
  await ana.goto('/maps')
  await ana.getByRole('button', { name: 'New map' }).click()
  await ana.getByRole('menuitem', { name: 'Matrix map' }).click()
  const dialog = ana.getByRole('dialog', { name: 'New matrix map' })
  await expect(dialog).toContainText('Prioritization matrix')
  await dialog.getByLabel('Name').fill('Review board')
  await dialog.getByRole('button', { name: 'Create map' }).click()
  await expect(ana.getByRole('heading', { level: 1, name: 'Review board' })).toBeVisible()
  // Feasibility 65 and impact 100: a star initiative; impact 5: a quick win; no impact yet: in the tray.
  await expect(ana.getByRole('button', { name: new RegExp(`${star}$`) })).toBeVisible()
  expect(await quadrantOf(ana, star)).toBe('q-tr')
  expect(await quadrantOf(ana, quick)).toBe('q-br')
  const tray = ana.getByRole('complementary', { name: 'Not yet assessed' })
  await expect(tray.getByText(waiting)).toBeVisible()
  await expect(tray.getByText(star)).toHaveCount(0)
  // Rated up in the side panel, the quick win moves up among the stars.
  await ana.getByRole('button', { name: new RegExp(`${quick}$`) }).click()
  const panel = ana.getByTestId('item-quick-edit')
  for (const label of ['Reach', 'Strategic fit'])
    await panel
      .getByRole('group', { name: label, exact: true })
      .first()
      .getByRole('button', { name: '5', exact: true })
      .click()
  await expect.poll(() => quadrantOf(ana, quick)).toBe('q-tr')
})

test('a map that is no longer shared disappears from the other device', async ({ browser }) => {
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
