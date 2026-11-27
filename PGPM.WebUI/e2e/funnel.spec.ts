import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { Api, adminApi, problems, pullAll, pushInserts, signInAs, uuidv7, type TestUser } from './helpers'

/**
 * The funnel walkthrough (docs/05 §3 phase 4 "Done when", docs/04 §4): with the seeded `core` + `innovation-ai`
 * packages, an innovation-team member captures an idea and assesses it – the playbook's ratings give the feasibility
 * index 58 / 65 –, puts it on the review board's agenda; the *Review board decision* section is editable only by the
 * group *Review board*; a board member decides *go*; the idea is converted into an *AI use case* project with the gates
 * G0–G3; gate decisions move the project's status as the template suggests.
 * Against the e2e stack, which seeds both packages on its first start (`Packages:SeedOnFirstStart`).
 */

/** The playbook's example (p. 34, docs/04 Appendix B): 53 + 5 (< 3 months) = 58. */
const ratings: [string, number][] = [
  ['We have access to the data', 5],
  ['We have the infrastructure', 4],
  ['We have the data quality', 4],
  ['We know how to solve the problem', 5],
  ['A similar solution has been built before', 4],
  ['We know suitable technologies', 5],
  ['No new processes are needed', 5],
  ['Existing processes are easy to adapt', 4],
  ['No substantial changes are needed', 4],
  ['Technical know-how is available', 4],
  ['Domain know-how is available', 5],
  ['General AI understanding is available', 4],
]

const contexts: BrowserContext[] = []
let admin: Api

async function user(baseURL: string | undefined, name: string, groupNames: string[]): Promise<TestUser> {
  const groups = await admin.call<{ id: string; name: string }[]>('GET', '/api/v1/groups')
  const groupIds = groupNames.map((n) => {
    const group = groups.find((g) => g.name === n)
    if (!group) throw new Error(`The seeded group ${n} is missing`)
    return group.id
  })
  const stamp = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`
  const username = `${name.toLowerCase()}-${stamp}`
  const created = await admin.call<{ user: { id: string }; temporaryPassword: { password: string } }>(
    'POST',
    '/api/v1/users',
    { username, displayName: `${name} ${stamp}`, groupIds },
  )
  const password = `e2e-${stamp}-${name.toLowerCase()}-pw`
  const own = await Api.create(baseURL)
  await own.signIn(username, created.temporaryPassword.password)
  await own.changePassword(created.temporaryPassword.password, password)
  await own.dispose()
  return { id: created.user.id, username, password, name: `${name} ${stamp}` }
}

async function page(browser: import('@playwright/test').Browser, who: TestUser, path: string) {
  const context = await browser.newContext()
  contexts.push(context)
  const p = await context.newPage()
  const seen = problems(p)
  await signInAs(p, who, path)
  await expect(p.getByTestId('sync-indicator')).toContainText('Live')
  return { page: p, seen }
}

/** A rating's button: `<label>: <value>`, or the value's label where it has one (0 and 5 here). */
const rate = (p: Page, label: string, value: number) => {
  const named = value === 5 ? '|fully applies' : value === 0 ? '|does not apply' : ''
  return p
    .getByRole('group', { name: label })
    .getByRole('button', { name: new RegExp(`^${label}: (${value}${named})$`) })
}

test.beforeEach(async ({ baseURL }) => {
  admin = await adminApi(baseURL)
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await admin.dispose()
})

test('funnel: idea → assessment 58 / 65 → review board → AI use case project → gate decisions', async ({
  browser,
  baseURL,
}) => {
  const ina = await user(baseURL, 'Ina', ['Innovation team', 'Members'])
  const rob = await user(baseURL, 'Rob', ['Review board', 'Members'])
  const title = `Invoice recognition ${Date.now().toString(36)}`

  // --- Ina captures and assesses the idea ---
  const { page: inaPage, seen: inaSeen } = await page(browser, ina, '/ideas')
  await inaPage.getByRole('button', { name: 'New idea' }).click()
  const dialog = inaPage.getByRole('dialog', { name: 'New idea' })
  await dialog.getByLabel('Title').fill(title)
  // Shared with her groups (Innovation team, Members) at edit – the review board sees it through Members. (The first
  // sync may still be bringing the statuses and groups: the form is complete once both are there.)
  await expect(dialog.getByRole('combobox', { name: 'Status' })).toContainText('New')
  await expect(dialog.locator('li', { hasText: 'Members' }).getByRole('combobox')).toContainText('Edit')
  await dialog.getByRole('button', { name: 'Create idea' }).click()
  await expect(inaPage.getByRole('heading', { level: 1, name: title })).toBeVisible()
  // The sections default for ideas (docs/04 §4.2).
  for (const name of ['Use case profile', 'Value', 'Feasibility', 'Risk & responsibility', 'Review board decision'])
    await expect(inaPage.getByRole('button', { name, exact: true })).toBeVisible()

  const feasibility = inaPage.locator('section', { has: inaPage.getByRole('button', { name: 'Feasibility' }) })
  await expect(feasibility.getByLabel('Feasibility index: incomplete').first()).toBeVisible()
  for (const [label, value] of ratings) await rate(inaPage, label, value).click()
  await feasibility.getByLabel('Time to a verified proof of value').click()
  await inaPage.getByRole('option', { name: /< 3 months/ }).click()
  await expect(feasibility.getByLabel('Feasibility index: 58 of 65').first()).toBeVisible()

  // The review board's section is read-only for her.
  const board = inaPage.locator('section', { has: inaPage.getByRole('button', { name: 'Review board decision' }) })
  await expect(board.getByLabel('Read-only for you')).toBeVisible()
  await expect(board.getByRole('combobox')).toHaveCount(0)
  // … and the server refuses a value there from her device: the section's rights are enforced, not only shown.
  const inaApi = await Api.create(baseURL)
  await inaApi.signIn(ina.username, ina.password)
  const decisionField = (await pullAll(inaApi)).field_definitions!.find((f) => f.key === 'rb_decision')!
  const ideaId = /\/ideas\/([0-9a-f-]{36})/.exec(inaPage.url())![1]!
  const go = (decisionField.config as { options: { id: string }[] }).options[0]!.id
  expect(
    await pushInserts(inaApi, [
      { table: 'field_values', row: { id: uuidv7(), itemId: ideaId, fieldId: decisionField.id, value: go } },
    ]),
  ).toEqual(['missing_access'])
  await inaApi.dispose()

  // On the agenda.
  await inaPage.getByLabel('Status', { exact: true }).click()
  await inaPage.getByRole('option', { name: 'Ready for review' }).click()
  const ideaUrl = inaPage.url()

  // --- Rob, on the review board, decides go ---
  const { page: robPage, seen: robSeen } = await page(browser, rob, '/ideas')
  await robPage.getByRole('button', { name: /Review board agenda|All ideas/ }).click()
  await robPage.getByRole('menuitem', { name: 'Review board agenda' }).click()
  await robPage.getByRole('link', { name: title }).click()
  await expect(robPage).toHaveURL(ideaUrl)
  const robBoard = robPage.locator('section', { has: robPage.getByRole('button', { name: 'Review board decision' }) })
  await expect(robBoard.getByLabel('Read-only for you')).toHaveCount(0)
  await robBoard.getByLabel('Decision', { exact: true }).click()
  await robPage.getByRole('option', { name: 'Go', exact: true }).click()
  // Live at Ina's.
  await expect(board.getByText('Go', { exact: true })).toBeVisible()

  // --- Ina converts it into an AI use case project ---
  await inaPage.getByRole('button', { name: 'Convert to project' }).click()
  const convert = inaPage.getByRole('dialog', { name: 'Convert to project' })
  await convert.getByLabel('Template').click()
  await inaPage.getByRole('option', { name: /AI use case/ }).click()
  await expect(convert.getByLabel('Status')).toContainText('Preparing test')
  await convert.getByRole('button', { name: 'Convert' }).click()
  await expect(inaPage).toHaveURL(/\/projects\//)
  await expect(inaPage.getByText('Preparing test').first()).toBeVisible()
  // The assessment came along.
  await expect(inaPage.getByLabel('Feasibility index: 58 of 65').first()).toBeVisible()

  // --- Gates G0–G3 with the playbook checklists ---
  await inaPage.getByRole('link', { name: 'Milestones' }).click()
  for (const gate of ['Ready for test', 'PoC decision', 'Scale decision', 'Impact review'])
    await expect(inaPage.getByText(gate, { exact: true }).first()).toBeVisible()

  const g0 = inaPage.locator('[data-testid^="milestone-"]', { hasText: 'Ready for test' })
  const boxes = await g0.getByRole('checkbox', { name: /^Done: / }).all()
  expect(boxes.length).toBeGreaterThan(0)
  for (const box of boxes) await box.check()
  await expect(g0.getByTestId('dod-progress')).toHaveText(`${boxes.length} / ${boxes.length}`)
  await g0.getByRole('radio', { name: 'Go', exact: true }).click()
  await g0.getByLabel('Decision note').fill('Sponsor and team named.')
  await g0.getByRole('button', { name: 'Record decision' }).click()
  await g0.getByRole('button', { name: 'Move project to Prototype / PoC' }).click()
  await expect(inaPage.getByText('Prototype / PoC').first()).toBeVisible()

  const g1 = inaPage.locator('[data-testid^="milestone-"]', { hasText: 'PoC decision' })
  await g1.getByRole('radio', { name: 'No-go', exact: true }).click()
  await g1.getByLabel('Decision note').fill('The accuracy stayed below the target.')
  await g1.getByRole('button', { name: 'Record decision' }).click()
  await g1.getByRole('button', { name: 'Move project to Stopped' }).click()
  await expect(inaPage.getByText('Stopped').first()).toBeVisible()

  // The stop / go record.
  await inaPage.goto('/gates')
  await expect(inaPage.getByRole('row', { name: new RegExp(`${title} Ready for test Go`) })).toBeVisible()
  await expect(inaPage.getByRole('row', { name: new RegExp(`${title} PoC decision No-go`) })).toBeVisible()

  expect(inaSeen).toEqual([])
  expect(robSeen).toEqual([])
})

// Using the configuration (the walkthrough, every other spec) changes none of it; statuses.spec.ts, which reorders the
// statuses, puts them back after itself, so this holds whichever ran first.
test('re-importing the built-in packages changes nothing (dry run all unchanged)', async () => {
  for (const name of ['core', 'innovation-ai']) {
    const diff = await admin.call<{ counts: { create: number; update: number; conflict: number } }>(
      'POST',
      '/api/v1/config/packages/dry-run',
      { builtIn: name },
    )
    expect(diff.counts).toMatchObject({ create: 0, update: 0, conflict: 0 })
  }
})
