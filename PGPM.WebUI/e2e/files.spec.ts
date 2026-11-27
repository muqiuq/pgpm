import { expect, test, type BrowserContext } from '@playwright/test'
import { createTeam, problems, signInAs, type Team } from './helpers'

/**
 * The offline file pipeline (docs/01 §4.5, docs/03 §10.2) in two browsers against the e2e stack: a file attached
 * while offline is stored on the device (*Pending upload*), goes up in chunks once the connection is back, and the
 * other device downloads it on demand.
 *
 * fixme until the server's `files` table and its chunk/commit/download endpoints are merged (the integration step
 * enables it).
 */

let team: Team
const contexts: BrowserContext[] = []

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL)
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await team.admin.dispose()
})

test.fixme('a file attached offline uploads on reconnect and downloads on the other device', async ({ browser }) => {
  const [ana, ben] = team.users
  const context = await browser.newContext()
  contexts.push(context)
  const page = await context.newPage()
  const seen = problems(page)
  await signInAs(page, ana!)
  await expect(page.getByTestId('sync-indicator')).toContainText('Live')

  await page.getByRole('button', { name: 'New note' }).click()
  const dialog = page.getByRole('dialog', { name: 'New note' })
  await dialog.getByLabel('Title').fill('With an attachment')
  await dialog.getByRole('button', { name: 'Create note' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'With an attachment' })).toBeVisible()
  await expect(page.getByText('Assigned at the first sync')).toHaveCount(0)
  const id = /\/notes\/([0-9a-f-]{36})/.exec(page.url())?.[1]
  expect(id).toBeTruthy()

  await context.setOffline(true)
  await expect(page.getByTestId('sync-indicator')).toContainText('Offline')
  await page.getByTestId('attachments-input').setInputFiles({
    name: 'minutes.txt',
    mimeType: 'text/plain',
    buffer: Buffer.from('Minutes of the kick-off: everybody agreed.'),
  })
  const line = page.getByTestId('attachments').locator('li', { hasText: 'minutes.txt' })
  await expect(line).toContainText('Pending upload')

  await context.setOffline(false)
  await expect(line).not.toContainText('Pending upload', { timeout: 20_000 })
  await expect(line).not.toContainText('Uploading')

  const other = await browser.newContext()
  contexts.push(other)
  const benPage = await other.newPage()
  const benSeen = problems(benPage)
  await signInAs(benPage, ben!, `/notes/${id}`)
  await expect(benPage.getByRole('heading', { level: 1, name: 'With an attachment' })).toBeVisible()
  const download = benPage.waitForEvent('download')
  await benPage.getByRole('button', { name: 'Download minutes.txt' }).click()
  expect((await download).suggestedFilename()).toBe('minutes.txt')

  expect(seen).toEqual([])
  expect(benSeen).toEqual([])
})
