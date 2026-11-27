import { expect, test, type BrowserContext } from '@playwright/test'
import { createTeam, localRows, offlineReady, pngWithMetadata, problems, signInAs, type Team } from './helpers'

/**
 * Item lists (docs/01 §4.3, docs/05 §3 phase 3): an idea created offline waits as *Not synced yet* with a pending key
 * (`I-…`), goes out when the connection is back – the server numbers it – and reaches a teammate's list, live. Needs
 * the phase 3 server (ideas creatable, the `core` statuses seeded).
 */
let team: Team
const contexts: BrowserContext[] = []

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL, ['Ana', 'Ben'], { Notes: 'Create', Ideas: 'Create' })
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await team.admin.dispose()
})

test('an idea created offline syncs on reconnect, with its key, and shows in the teammate’s list', async ({
  browser,
}) => {
  const [ana, ben] = team.users as [(typeof team.users)[0], (typeof team.users)[0]]
  const title = `Offline idea ${Date.now().toString(36)}`

  const anaContext = await browser.newContext()
  contexts.push(anaContext)
  const anaPage = await anaContext.newPage()
  const anaSeen = problems(anaPage)
  await signInAs(anaPage, ana, '/ideas')
  await expect(anaPage.getByRole('heading', { level: 1, name: 'Ideas' })).toBeVisible()
  await expect(anaPage.getByTestId('sync-indicator')).toContainText('Live')

  const benContext = await browser.newContext()
  contexts.push(benContext)
  const benPage = await benContext.newPage()
  const benSeen = problems(benPage)
  await signInAs(benPage, ben, '/ideas')
  await expect(benPage.getByRole('heading', { level: 1, name: 'Ideas' })).toBeVisible()

  await offlineReady(anaPage)
  await anaContext.setOffline(true)
  await expect(anaPage.getByTestId('sync-indicator')).toContainText('Offline')
  await anaPage.getByRole('button', { name: 'New idea' }).click()
  const dialog = anaPage.getByRole('dialog', { name: 'New idea' })
  await dialog.getByLabel('Title').fill(title)
  // The default status is preselected, the team at edit.
  await expect(dialog.getByLabel('Status')).toContainText('New')
  await expect(dialog.locator('li', { hasText: team.group.name }).getByRole('combobox')).toContainText('Edit')
  await dialog.getByRole('button', { name: 'Create idea' }).click()
  await expect(anaPage.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await expect(anaPage.getByText('Not synced yet').first()).toBeVisible()

  await anaPage
    .getByRole('complementary', { name: 'Navigation' })
    .getByRole('link', { name: 'Ideas', exact: true })
    .click()
  const row = anaPage.getByRole('row', { name: new RegExp(title) })
  await expect(row).toContainText('I-…')

  await anaContext.setOffline(false)
  await expect(anaPage.getByTestId('sync-pending')).toHaveCount(0, { timeout: 15_000 })
  await expect(row).toContainText(/I-\d+/)
  const key = (await row.textContent())?.match(/I-\d+/)?.[0]

  // Ben's list has it, with the same key, live.
  const benRow = benPage.getByRole('row', { name: new RegExp(title) })
  await expect(benRow).toBeVisible()
  await expect(benRow).toContainText(key!)

  // The quick filter and the search find it.
  await benPage.getByPlaceholder('Search ideas…').fill(key!)
  await expect(benPage.getByRole('row', { name: new RegExp(title) })).toBeVisible()

  expect(anaSeen).toEqual([])
  expect(benSeen).toEqual([])
})

/**
 * Idea → project (docs/01 §8, docs/03 §12.8), offline: the project gets the idea's title, description, persons and
 * sharing, its cover and attachments (copies sharing the stored bytes: nothing is uploaded again), its origin and a
 * *related* link back; the idea moves to *Selected* and shows *Converted to P-…* once the server has numbered the project
 * (and set the idea's `convertedToItemId`). The cover is a PNG with metadata: the server strips it at the commit, the
 * name gets the type's extension.
 */
test('an idea converted into a project offline syncs with origin, sharing and the idea’s new status', async ({
  browser,
  baseURL,
}) => {
  const convertTeam = await createTeam(baseURL, ['Cleo'], { Ideas: 'Create', Projects: 'Create' })
  try {
    const [cleo] = convertTeam.users as [(typeof convertTeam.users)[0]]
    const title = `Idea to convert ${Date.now().toString(36)}`
    const context = await browser.newContext()
    contexts.push(context)
    const page = await context.newPage()
    const seen = problems(page)
    await signInAs(page, cleo, '/ideas')
    await expect(page.getByTestId('sync-indicator')).toContainText('Live')

    await page.getByRole('button', { name: 'New idea' }).click()
    const dialog = page.getByRole('dialog', { name: 'New idea' })
    await dialog.getByLabel('Title').fill(title)
    await dialog.getByRole('button', { name: 'Create idea' }).click()
    await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
    await expect(page.getByText('Assigned at the first sync')).toHaveCount(0)
    const ideaUrl = page.url()
    const ideaId = /\/ideas\/([0-9a-f-]{36})/.exec(ideaUrl)![1]

    // Cleo herself as the responsible person.
    await page.getByRole('button', { name: 'Assign person' }).click()
    await page.locator('#assign-person').fill(cleo.name)
    await page.getByRole('option', { name: new RegExp(cleo.name) }).click()
    await page.locator('#assign-role').fill('Responsible')
    await page.keyboard.press('Escape')
    await page.getByRole('button', { name: 'Assign', exact: true }).click()
    await expect(page.getByTestId('assignments')).toContainText('Responsible')

    // A cover and an attachment, uploaded (the cover first: uploads go in order).
    const cover = pngWithMetadata()
    await page.getByTestId('cover-input').setInputFiles({ name: 'Sketch', mimeType: 'image/png', buffer: cover })
    await expect(page.getByRole('img', { name: 'Cover image' })).toBeVisible()
    await page.getByTestId('attachments-input').setInputFiles({
      name: 'minutes.txt',
      mimeType: 'text/plain',
      buffer: Buffer.from('Minutes of the review board.'),
    })
    const filesOf = async (itemId: string) =>
      (await localRows(page, 'files'))
        .filter((f) => f.itemId === itemId && f.deleted !== true)
        .map((f) => `${String(f.purpose)} ${String(f.fileName)} ${String(f.status)}`)
        .sort()
    await expect
      .poll(() => filesOf(ideaId!), { timeout: 20_000 })
      .toEqual(['attachment minutes.txt available', 'cover Sketch.png available'])
    const stored = (await localRows(page, 'files')).find((f) => f.itemId === ideaId && f.purpose === 'cover')!
    expect(stored.sizeBytes).toBeLessThan(cover.length)

    await offlineReady(page)
    await context.setOffline(true)
    await expect(page.getByTestId('sync-indicator')).toContainText('Offline')
    await page.getByRole('button', { name: 'Convert to project' }).click()
    const convert = page.getByRole('dialog', { name: 'Convert to project' })
    await expect(convert.getByLabel('Project title')).toHaveValue(title)
    await expect(convert).toContainText('moves to Selected')
    await convert.getByRole('button', { name: 'Convert' }).click()
    await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/)
    await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
    await expect(page.getByText('Not synced yet').first()).toBeVisible()

    await expect(page.getByRole('img', { name: 'Cover image' })).toBeVisible()
    const projectId = /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1]

    await context.setOffline(false)
    await expect(page.getByTestId('sync-pending')).toHaveCount(0, { timeout: 15_000 })
    await expect(page.getByText(/P-\d{4}/).first()).toBeVisible()
    await expect(page.getByText('Origin')).toBeVisible()
    // The copies are stored at once (the same bytes), the project's cover is its own file.
    await expect
      .poll(() => filesOf(projectId!), { timeout: 15_000 })
      .toEqual(['attachment minutes.txt available', 'cover Sketch.png available'])
    await expect(page.getByTestId('assignments')).toContainText(cleo.name)
    await expect(page.getByTestId('assignments')).toContainText('Responsible')
    await expect(page.getByTestId('assignments').getByLabel('Not synced yet')).toHaveCount(0)
    const attachment = page.getByTestId('attachments').locator('li', { hasText: 'minutes.txt' })
    await expect(attachment).toBeVisible()
    await expect(attachment).not.toContainText(/Pending upload|Uploading|Not uploaded yet|Failed/)
    await page.getByRole('link', { name: 'Links', exact: true }).click()
    await expect(page.getByRole('link', { name: title })).toBeVisible()

    await page.goto(ideaUrl)
    await expect(page.getByText('Selected').first()).toBeVisible()
    await expect(page.getByText(/Converted to P-\d{4}/)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Convert to project' })).toHaveCount(0)
    expect(seen).toEqual([])
  } finally {
    await convertTeam.admin.dispose()
  }
})
