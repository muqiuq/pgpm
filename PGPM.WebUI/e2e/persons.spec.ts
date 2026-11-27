import { expect, test, type BrowserContext } from '@playwright/test'
import { createTeam, problems, signInAs, type Team } from './helpers'

/**
 * The persons directory (docs/01 §16, docs/05 §3 phase 3): a manual person created offline by someone with Persons:
 * Edit syncs when the connection is back; another device finds it in the list and with the global search (Ctrl K);
 * deactivating it hides it from the default list.
 */
let team: Team
const contexts: BrowserContext[] = []

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL, ['Ana', 'Ben'], { Notes: 'Create', Persons: 'Edit' })
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await team.admin.dispose()
})

test('a manual person created offline syncs, is found by search and can be deactivated', async ({ browser }) => {
  const [ana, ben] = team.users as [(typeof team.users)[0], (typeof team.users)[0]]
  const name = `Eva Extern ${Date.now().toString(36)}`

  const anaContext = await browser.newContext()
  contexts.push(anaContext)
  const anaPage = await anaContext.newPage()
  const anaSeen = problems(anaPage)
  await signInAs(anaPage, ana, '/persons')
  await expect(anaPage.getByRole('heading', { level: 1, name: 'Persons' })).toBeVisible()

  await anaContext.setOffline(true)
  await anaPage.getByRole('button', { name: 'New person' }).click()
  const dialog = anaPage.getByRole('dialog', { name: 'New person' })
  await dialog.getByLabel('Name', { exact: true }).fill(name)
  await dialog.getByLabel('E-mail').fill('Eva.Extern@Partner.Example')
  await dialog.getByLabel('Company').fill('Partner AG')
  await dialog.getByRole('button', { name: 'Create person' }).click()
  await expect(anaPage.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(anaPage.getByText('Not synced yet')).toBeVisible()
  await anaContext.setOffline(false)
  await expect(anaPage.getByText('Not synced yet')).toHaveCount(0, { timeout: 15_000 })

  const benContext = await browser.newContext()
  contexts.push(benContext)
  const benPage = await benContext.newPage()
  const benSeen = problems(benPage)
  await signInAs(benPage, ben, '/persons')
  await benPage.getByLabel('Search persons…').fill('partner.example')
  await expect(benPage.getByRole('link', { name, exact: true })).toBeVisible()

  // The global search finds it too (offline-capable, the local data).
  await benPage.keyboard.press('Control+k')
  await benPage.keyboard.type(name)
  await benPage.getByRole('option', { name: new RegExp(name) }).click()
  await expect(benPage.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(benPage.getByRole('link', { name: 'eva.extern@partner.example' })).toBeVisible()

  await benPage.getByRole('button', { name: 'More person actions' }).click()
  await benPage.getByRole('menuitem', { name: 'Deactivate' }).click()
  await expect(benPage.getByText('Inactive', { exact: true })).toBeVisible()

  // Ana's list shows active persons only, unless asked.
  await anaPage.getByRole('complementary', { name: 'Navigation' }).getByRole('link', { name: 'Persons' }).click()
  await anaPage.getByLabel('Search persons…').fill(name)
  await expect(anaPage.getByRole('link', { name, exact: true })).toHaveCount(0)
  await anaPage.getByRole('button', { name: 'With inactive' }).click()
  await expect(anaPage.getByRole('link', { name, exact: true })).toBeVisible()

  expect(anaSeen).toEqual([])
  expect(benSeen).toEqual([])
})
