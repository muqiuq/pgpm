import { expect, test, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test'
import { createTeam, problems, signIn, signInAs, signOut, type Team, type TestUser } from './helpers'

/**
 * The sync core in two browsers (docs/05 §3 phase 2 "done when", docs/03 §11.13): browser contexts are devices, each
 * signed in as a user of a team of its own (a group with Notes: Create, created through the admin API), against the
 * e2e stack – the real server, its live channel and the client's IndexedDB. Live changes must arrive in under a second
 * (measured from the click on one page to the text on the other, both in the browser's clock).
 */

interface Device {
  context: BrowserContext
  page: Page
  seen: string[]
  /** What the live channel received, every socket of the page. */
  frames: string[]
}

let team: Team
const contexts: BrowserContext[] = []

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL)
})

test.afterEach(async () => {
  for (const context of contexts.splice(0)) await context.close()
  await team.admin.dispose()
})

/** A new device (browser context) signed in as the user, on the Notes, live. */
async function device(browser: Browser, user: TestUser): Promise<Device> {
  const context = await browser.newContext()
  contexts.push(context)
  const page = await context.newPage()
  const seen = problems(page)
  const frames: string[] = []
  page.on('websocket', (ws) => ws.on('framereceived', (frame) => frames.push(String(frame.payload))))
  await signInAs(page, user)
  await expect(page.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
  await expect(indicator(page)).toContainText('Live')
  return { context, page, seen, frames }
}

const indicator = (page: Page) => page.getByTestId('sync-indicator')

/** The Notes entry of the navigation (the breadcrumb has one too). */
const notesLink = (page: Page) =>
  page.getByRole('complementary', { name: 'Navigation' }).getByRole('link', { name: 'Notes', exact: true })

/** The page's errors, without what going offline on purpose and the conflicts' toasts bring. */
function expectNoProblems(...devices: Device[]) {
  for (const d of devices) expect(d.seen).toEqual([])
}

/** New note → title, *Share with* the team at the access (preselected: edit) → Create; answers its id. */
async function createNote(page: Page, title: string, access?: 'Read' | 'Edit' | 'Manage' | 'No access') {
  await notesLink(page).click()
  await page.getByRole('button', { name: 'New note' }).click()
  const dialog = page.getByRole('dialog', { name: 'New note' })
  await dialog.getByLabel('Title').fill(title)
  const share = dialog.locator('li', { hasText: team.group.name }).getByRole('combobox')
  await expect(share).toContainText('Edit')
  if (access && access !== 'Edit') {
    await share.click()
    await page.getByRole('option', { name: access, exact: true }).click()
  }
  await dialog.getByRole('button', { name: 'Create note' }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
  const id = /\/notes\/([0-9a-f-]{36})/.exec(page.url())?.[1]
  expect(id).toBeTruthy()
  // Sent: the number assigned by the server.
  await expect(page.getByText('Assigned at the first sync')).toHaveCount(0)
  return id!
}

async function openNote(page: Page, title: string) {
  await notesLink(page).click()
  await page.getByRole('link', { name: title, exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
}

/** The inline title of the open note: edit, type, Enter. */
async function editTitle(page: Page, title: string) {
  await page.getByRole('button', { name: 'Edit Title' }).click()
  await page.getByRole('textbox', { name: 'Title' }).fill(title)
  await page.getByRole('textbox', { name: 'Title' }).press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
}

/** ⋯ → Delete note… → Delete: to the Trash, back on the list. */
async function deleteNote(page: Page) {
  await page.getByRole('button', { name: 'More note actions' }).click()
  await page.getByRole('menuitem', { name: 'Delete note…' }).click()
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
}

/** How many rows a store of the device's IndexedDB holds (0 without the database). */
function localCount(page: Page, store: string) {
  return page.evaluate(async (store) => {
    if (!(await indexedDB.databases()).some((d) => d.name === 'pgpm')) return 0
    return new Promise<number>((resolve) => {
      const open = indexedDB.open('pgpm')
      open.onerror = () => resolve(0)
      open.onsuccess = () => {
        const db = open.result
        if (!db.objectStoreNames.contains(store)) {
          db.close()
          resolve(0)
          return
        }
        const count = db.transaction(store, 'readonly').objectStore(store).count()
        count.onsuccess = () => {
          resolve(count.result)
          db.close()
        }
        count.onerror = () => {
          resolve(0)
          db.close()
        }
      }
    })
  }, store)
}

/** Until the device has sent everything and heard back (its outbox is empty): its changes are on the server. */
async function sent(page: Page) {
  await expect.poll(() => localCount(page, 'outbox'), { timeout: 10_000 }).toBe(0)
}

/** A row of the synced table in the device's IndexedDB (null: not there, or no database). */
function localRow(page: Page, table: string, id: string) {
  return page.evaluate(
    async ([table, id]) => {
      if (!(await indexedDB.databases()).some((d) => d.name === 'pgpm')) return null
      return new Promise<unknown>((resolve) => {
        const open = indexedDB.open('pgpm')
        open.onerror = () => resolve(null)
        open.onsuccess = () => {
          const db = open.result
          if (!db.objectStoreNames.contains(table!)) {
            db.close()
            resolve(null)
            return
          }
          const get = db.transaction(table!, 'readonly').objectStore(table!).get(id!)
          get.onsuccess = () => {
            resolve(get.result ?? null)
            db.close()
          }
          get.onerror = () => {
            resolve(null)
            db.close()
          }
        }
      })
    },
    [table, id],
  )
}

/**
 * The time from a click on one page to a text appearing on the other, in ms – both read from the browser's clock (the
 * contexts share one browser process), the text watched by a MutationObserver.
 */
async function latency(from: Page, click: Locator, to: Page, text: string): Promise<number> {
  await to.evaluate((text) => {
    const w = window as unknown as { pgpmSeen?: Promise<number> }
    w.pgpmSeen = new Promise<number>((resolve) => {
      const seen = () => document.body.innerText.includes(text)
      const observer = new MutationObserver(() => {
        if (seen()) {
          observer.disconnect()
          resolve(Date.now())
        }
      })
      observer.observe(document.body, { subtree: true, childList: true, characterData: true })
    })
  }, text)
  await from.evaluate(() => {
    document.addEventListener(
      'click',
      () => ((window as unknown as { pgpmClicked: number }).pgpmClicked = Date.now()),
      {
        capture: true,
        once: true,
      },
    )
  })
  await click.click()
  const seenAt = await to.evaluate(() =>
    Promise.race([
      (window as unknown as { pgpmSeen: Promise<number> }).pgpmSeen,
      new Promise<number>((resolve) => setTimeout(() => resolve(Number.NaN), 10_000)),
    ]),
  )
  const clickedAt = await from.evaluate(() => (window as unknown as { pgpmClicked: number }).pgpmClicked)
  return seenAt - clickedAt
}

/** Opens the sync dialog on the conflicts and decides the note's conflict. */
async function resolve(page: Page, noteId: string, action: string, toast: string) {
  await indicator(page).click()
  const dialog = page.getByTestId('sync-dialog')
  const conflict = dialog.getByTestId(`conflict-${noteId}`)
  await expect(conflict).toBeVisible()
  await conflict.getByRole('button', { name: action, exact: true }).click()
  await expect(page.getByText(toast)).toBeVisible()
  await dialog.getByRole('button', { name: 'Close', exact: true }).last().click()
}

test.describe('sync between two browsers', () => {
  test('(a) a note created and edited by Ana appears at Ben live, in under a second', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)

    // Created: from Ana's click on Create to the row in Ben's list.
    await ana.page.getByRole('button', { name: 'New note' }).click()
    const dialog = ana.page.getByRole('dialog', { name: 'New note' })
    await dialog.getByLabel('Title').fill('Live note')
    await expect(dialog.locator('li', { hasText: team.group.name }).getByRole('combobox')).toContainText('Edit')
    const created = await latency(ana.page, dialog.getByRole('button', { name: 'Create note' }), ben.page, 'Live note')
    await expect(ana.page.getByRole('heading', { level: 1, name: 'Live note' })).toBeVisible()

    // Edited by Ana on her page, watched by Ben on his.
    await openNote(ben.page, 'Live note')
    await ana.page.getByRole('button', { name: 'Edit Title' }).click()
    await ana.page.getByRole('textbox', { name: 'Title' }).fill('Live note, edited')
    const edited = await latency(
      ana.page,
      ana.page.getByRole('button', { name: 'Save Title' }),
      ben.page,
      'Live note, edited',
    )
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Live note, edited' })).toBeVisible()

    // And back: Ben's edit at Ana.
    await ben.page.getByRole('button', { name: 'Edit Title' }).click()
    await ben.page.getByRole('textbox', { name: 'Title' }).fill('Live note, by Ben')
    const back = await latency(
      ben.page,
      ben.page.getByRole('button', { name: 'Save Title' }),
      ana.page,
      'Live note, by Ben',
    )

    console.log(`live latency: created ${created} ms, edited ${edited} ms, back ${back} ms`)
    test.info().annotations.push({ type: 'live latency (ms)', description: `${created} / ${edited} / ${back}` })
    for (const ms of [created, edited, back]) {
      expect(ms).toBeGreaterThan(0)
      expect(ms).toBeLessThan(1000)
    }
    for (const page of [ana.page, ben.page]) {
      await expect(indicator(page)).toContainText('Live')
      await expect(page.getByTestId('sync-pending')).toHaveCount(0)
    }
    expectNoProblems(ana, ben)
  })

  test('(b) an offline edit of the same field becomes a conflict: Keep mine, then Keep theirs', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    const id = await createNote(ana.page, 'Conflicting')
    await openNote(ben.page, 'Conflicting')

    // Ben offline: his title and pin wait; Ana changes the title meanwhile.
    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toContainText('Offline')
    await editTitle(ana.page, 'Title by Ana')
    await sent(ana.page)
    await editTitle(ben.page, 'Title by Ben')
    await ben.page.getByRole('button', { name: 'Pin', exact: true }).click()
    await expect(ben.page.getByTestId('sync-pending')).toHaveText('1')

    // Online: the pin goes through for both, the title is a conflict (shown once as a toast, orange count).
    await ben.context.setOffline(false)
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveText('1')
    await expect(ben.page.getByText('1 sync conflict')).toBeVisible()
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Title by Ana' })).toBeVisible()
    await expect(ana.page.getByRole('button', { name: 'Unpin' })).toBeVisible()
    await expect(ben.page.getByTestId('sync-pending')).toHaveCount(0)

    // Yours / theirs with who, then Keep mine: Ana gets Ben's title live.
    await indicator(ben.page).click()
    const conflict = ben.page.getByTestId(`conflict-${id}`)
    await expect(conflict).toContainText('Title by Ben')
    await expect(conflict).toContainText('Title by Ana')
    await expect(conflict).toContainText(anaUser!.name)
    await conflict.getByRole('button', { name: 'Keep mine', exact: true }).click()
    await expect(ben.page.getByText('Your version is kept')).toBeVisible()
    await ben.page.getByTestId('sync-dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
    await expect(ana.page.getByRole('heading', { level: 1, name: 'Title by Ben' })).toBeVisible()
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveCount(0)

    // Again, and Keep theirs: Ben's value goes, Ana's stays everywhere.
    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toContainText('Offline')
    await editTitle(ana.page, 'Ana again')
    await sent(ana.page)
    await editTitle(ben.page, 'Ben again')
    await ben.context.setOffline(false)
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveText('1')
    await resolve(ben.page, id, 'Keep theirs', 'Their version is kept')
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Ana again' })).toBeVisible()
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveCount(0)
    await expect(ana.page.getByRole('heading', { level: 1, name: 'Ana again' })).toBeVisible()

    // The History (online) has the conflicts and their resolutions.
    await ben.page.getByRole('link', { name: 'History', exact: true }).click()
    await expect(ben.page.getByText('Conflict resolved').first()).toBeVisible()
    expectNoProblems(ana, ben)
  })

  test('(c) delete against edit, both ways: Restore with my changes, and Delete anyway', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)

    // Ana deletes what Ben (manage: he may restore) edits offline → Restore with my changes: back for both, with
    // Ben's title.
    const first = await createNote(ana.page, 'Deleted by Ana', 'Manage')
    await openNote(ben.page, 'Deleted by Ana')
    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toContainText('Offline')
    await editTitle(ben.page, 'Edited by Ben')
    await deleteNote(ana.page)
    await sent(ana.page)
    await ben.context.setOffline(false)
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveText('1')
    await indicator(ben.page).click()
    const deleted = ben.page.getByTestId(`conflict-${first}`)
    await expect(deleted).toContainText('Deleted')
    await deleted.getByRole('button', { name: 'Restore with my changes' }).click()
    await expect(ben.page.getByText('Restored with your changes')).toBeVisible()
    await ben.page.getByTestId('sync-dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Edited by Ben' })).toBeVisible()
    await notesLink(ana.page).click()
    await expect(ana.page.getByRole('link', { name: 'Edited by Ben', exact: true })).toBeVisible()

    // Ben (manage) deletes offline what Ana changes meanwhile → Delete anyway: gone for both.
    const second = await createNote(ana.page, 'Changed by Ana', 'Manage')
    await openNote(ben.page, 'Changed by Ana')
    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toContainText('Offline')
    await deleteNote(ben.page)
    await expect(ben.page.getByRole('link', { name: 'Changed by Ana', exact: true })).toHaveCount(0)
    await editTitle(ana.page, 'Changed by Ana again')
    await sent(ana.page)
    await ben.context.setOffline(false)
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveText('1')
    // The delete was held back: the note is there again, with Ana's change.
    await expect(ben.page.getByRole('link', { name: 'Changed by Ana again', exact: true })).toBeVisible()
    await indicator(ben.page).click()
    const changed = ben.page.getByTestId(`conflict-${second}`)
    await expect(changed).toContainText('Changed')
    await changed.getByRole('button', { name: 'Delete anyway' }).click()
    await expect(changed).toHaveCount(0)
    await ben.page.getByTestId('sync-dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
    await expect(ben.page.getByRole('link', { name: 'Changed by Ana again', exact: true })).toHaveCount(0)
    await expect(ana.page.getByText('This note was deleted.')).toBeVisible()
    await expect(ben.page.getByTestId('sync-conflicts')).toHaveCount(0)
    expectNoProblems(ana, ben)
  })

  test('(d) unsharing removes the note from Ben’s device live (eviction)', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    const id = await createNote(ana.page, 'Soon private')
    await openNote(ben.page, 'Soon private')
    expect(await localRow(ben.page, 'items', id)).toMatchObject({ title: 'Soon private', access: 'edit' })

    await ana.page.getByRole('button', { name: /^Share · / }).click()
    const dialog = ana.page.getByRole('dialog', { name: 'Share Soon private' })
    await dialog.locator('li', { hasText: team.group.name }).getByRole('combobox').click()
    await ana.page.getByRole('option', { name: 'No access', exact: true }).click()
    await dialog.getByRole('button', { name: 'Save', exact: true }).click()
    await expect(ana.page.getByRole('button', { name: 'Share · Private' })).toBeVisible()

    await expect(ben.page.getByText('This note is not on this device.')).toBeVisible()
    await expect(ben.page.getByText('Access to “Soon private” was removed')).toBeVisible()
    expect(await localRow(ben.page, 'items', id)).toBeNull()
    await notesLink(ben.page).click()
    await expect(ben.page.getByRole('link', { name: 'Soon private', exact: true })).toHaveCount(0)

    // Shared again, read only: back at Ben without edit.
    await ana.page.getByRole('button', { name: 'Share · Private' }).click()
    await dialog.locator('li', { hasText: team.group.name }).getByRole('combobox').click()
    await ana.page.getByRole('option', { name: 'Read', exact: true }).click()
    await dialog.getByRole('button', { name: 'Save', exact: true }).click()
    await ben.page.getByRole('link', { name: 'Soon private', exact: true }).click()
    await expect(ben.page.getByText('Read only')).toBeVisible()
    await expect(ben.page.getByRole('button', { name: 'Edit Title' })).toHaveCount(0)
    expectNoProblems(ana, ben)
  })

  test('(e) the admin changes Ben’s groups: his device resets, keeps his offline note and goes on', async ({
    browser,
  }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    await createNote(ana.page, 'Before the reset')
    await openNote(ben.page, 'Before the reset')
    const other = await team.admin.call<{ id: string }>('POST', '/api/v1/groups', { name: `${team.group.name} 2` })
    const update = (groupIds: string[]) =>
      team.admin.call('PUT', `/api/v1/users/${benUser!.id}`, { displayName: benUser!.name, groupIds })

    // Live: the server says reset (access epoch +1, then close 4409); Ben's device pulls everything again from 0.
    const pulledFromZero = ben.page.waitForRequest(
      (r) => r.url().endsWith('/api/v1/sync') && (r.postDataJSON() as { since: number }).since === 0,
    )
    await update([team.group.id, other.id])
    await pulledFromZero
    expect(ben.frames.some((f) => JSON.parse(f).type === 'reset')).toBe(true)
    await expect(indicator(ben.page)).toContainText('Live')
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Before the reset' })).toBeVisible()
    await editTitle(ben.page, 'After the reset')
    await expect(ana.page.getByRole('heading', { level: 1, name: 'After the reset' })).toBeVisible()

    // Offline, a new note; meanwhile the admin changes his groups again: the note goes out first, then the reset.
    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toContainText('Offline')
    await createNoteOffline(ben.page, 'Created offline by Ben')
    await update([team.group.id])
    await ben.context.setOffline(false)
    await expect(indicator(ben.page)).toContainText('Live')
    await expect(ben.page.getByTestId('sync-pending')).toHaveCount(0)
    await notesLink(ana.page).click()
    await expect(ana.page.getByRole('link', { name: 'Created offline by Ben', exact: true })).toBeVisible()
    await notesLink(ben.page).click()
    await expect(ben.page.getByRole('link', { name: 'Created offline by Ben', exact: true })).toBeVisible()
    await expect(ben.page.getByRole('link', { name: 'After the reset', exact: true })).toBeVisible()
    expectNoProblems(ana, ben)
  })

  test('(f) delete → Trash → Restore: visible again for both', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    await createNote(ana.page, 'To the trash')
    await expect(ben.page.getByRole('link', { name: 'To the trash', exact: true })).toBeVisible()
    await deleteNote(ana.page)
    await expect(ana.page.getByText('Note deleted')).toBeVisible()
    await expect(ben.page.getByRole('link', { name: 'To the trash', exact: true })).toHaveCount(0)

    await ana.page.getByRole('link', { name: 'Trash', exact: true }).click()
    const row = ana.page.getByRole('row', { name: /To the trash/ })
    await expect(row).toContainText(anaUser!.name)
    await row.getByRole('button', { name: 'Restore', exact: true }).click()
    await expect(ana.page.getByText('To the trash is restored')).toBeVisible()
    await expect(ana.page.getByRole('row', { name: /To the trash/ })).toHaveCount(0)

    await expect(ben.page.getByRole('link', { name: 'To the trash', exact: true })).toBeVisible()
    await notesLink(ana.page).click()
    await expect(ana.page.getByRole('link', { name: 'To the trash', exact: true })).toBeVisible()
    expectNoProblems(ana, ben)
  })

  test('(g) signing out wipes the device; another user signing in never sees the previous one’s notes', async ({
    browser,
  }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const id = await createNote(ana.page, 'Ana’s private note', 'No access')
    expect(await localRow(ana.page, 'items', id)).toMatchObject({ title: 'Ana’s private note' })

    await signOut(ana.page)
    // The database went with everything in it (a view still open may have created an empty one since).
    expect(await localRow(ana.page, 'items', id)).toBeNull()
    for (const store of ['items', 'item_shares', 'groups', 'persons', 'outbox', 'meta']) {
      expect(await localCount(ana.page, store), store).toBe(0)
    }

    // Ana again, then her session ends without a sign-out (the cookie gone): Ben signs in on the same browser.
    await signInAs(ana.page, anaUser!)
    await expect(ana.page.getByRole('link', { name: 'Ana’s private note', exact: true })).toBeVisible()
    await ana.context.clearCookies()
    await ana.page.goto('/notes')
    await expect(ana.page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
    // Watched from the sign-in on (the same document): Ana's note must never show, not even for a moment.
    await ana.page.evaluate(() => {
      const w = window as unknown as { pgpmLeak: boolean }
      w.pgpmLeak = false
      new MutationObserver(() => {
        if (document.body.innerText.includes('Ana’s private note')) w.pgpmLeak = true
      }).observe(document.body, { subtree: true, childList: true, characterData: true })
    })
    await signIn(ana.page, benUser!.username, benUser!.password)
    const later = ana.page.getByRole('button', { name: 'Later' })
    await expect(later.or(ana.page.getByTestId('user-menu'))).toBeVisible()
    if (await later.isVisible()) await later.click()
    await expect(indicator(ana.page)).toContainText('Live')
    await notesLink(ana.page).click()
    await expect(ana.page.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
    await expect(ana.page.getByRole('link', { name: 'Ana’s private note', exact: true })).toHaveCount(0)
    expect(await localRow(ana.page, 'items', id)).toBeNull()
    expect(await ana.page.evaluate(() => (window as unknown as { pgpmLeak: boolean }).pgpmLeak)).toBe(false)
    expectNoProblems(ana)
  })

  test('(h) two tabs of one user: one holds the live connection, both update', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ben = await device(browser, benUser!)
    const context = await browser.newContext()
    contexts.push(context)
    const sockets = new Map<Page, number>()
    const watch = (page: Page) =>
      page.on('websocket', (ws) => {
        if (!ws.url().endsWith('/api/v1/sync/live')) return
        sockets.set(page, (sockets.get(page) ?? 0) + 1)
        ws.on('close', () => sockets.set(page, (sockets.get(page) ?? 1) - 1))
      })
    const first = await context.newPage()
    watch(first)
    await signInAs(first, anaUser!)
    await expect(indicator(first)).toContainText('Live')
    const second = await context.newPage()
    watch(second)
    await second.goto('/notes')
    await expect(second.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
    await expect(indicator(second)).toContainText('Live')
    await expect.poll(() => [sockets.get(first) ?? 0, sockets.get(second) ?? 0]).toEqual([1, 0])

    // The follower tab writes; the leader pushes; both tabs and Ben see it.
    await createNote(second, 'From the second tab')
    await expect(ben.page.getByRole('link', { name: 'From the second tab', exact: true })).toBeVisible()
    await notesLink(first).click()
    await expect(first.getByRole('link', { name: 'From the second tab', exact: true })).toBeVisible()
    await indicator(second).click()
    await expect(second.getByText('Another tab of PGPM runs the sync')).toBeVisible()
    await second.getByTestId('sync-dialog').getByRole('button', { name: 'Close', exact: true }).last().click()

    // Ben's change reaches both tabs; the leader closes, the follower takes over the live connection.
    await openNote(ben.page, 'From the second tab')
    await editTitle(ben.page, 'Edited by Ben for both tabs')
    await expect(first.getByRole('link', { name: 'Edited by Ben for both tabs', exact: true })).toBeVisible()
    await expect(second.getByRole('heading', { level: 1, name: 'Edited by Ben for both tabs' })).toBeVisible()
    await first.close()
    await expect.poll(() => sockets.get(second) ?? 0, { timeout: 15_000 }).toBe(1)
    await expect(indicator(second)).toContainText('Live')
    await editTitle(ben.page, 'Seen by the new leader')
    await expect(second.getByRole('heading', { level: 1, name: 'Seen by the new leader' })).toBeVisible()
    expectNoProblems(ben)
  })

  test('(i) the other user sees “… is editing”, never their own', async ({ browser }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    await createNote(ana.page, 'Being edited')
    await openNote(ben.page, 'Being edited')

    const edit = (page: Page) => page.getByRole('button', { name: 'Edit', exact: true }).click()
    await edit(ana.page)
    await expect(ben.page.getByText(`${anaUser!.name} is editing`)).toBeVisible()
    await edit(ben.page)
    await expect(ben.page.getByText(`${anaUser!.name} is editing this`)).toBeVisible()
    await expect(ana.page.getByText(`${benUser!.name} is editing this`)).toBeVisible()
    await expect(ana.page.getByText(`${anaUser!.name} is editing`)).toHaveCount(0)
    await expect(ben.page.getByText(`${benUser!.name} is editing`)).toHaveCount(0)

    // Ana leaves the editor: the notice goes at Ben.
    await ana.page.getByRole('button', { name: 'Cancel', exact: true }).click()
    await expect(ana.page.getByRole('heading', { level: 1, name: 'Being edited' })).toBeVisible()
    await expect(ben.page.getByText(`${anaUser!.name} is editing this`)).toHaveCount(0)
    await expect(ana.page.getByText(`${benUser!.name} is editing`)).toBeVisible()
    expectNoProblems(ana, ben)
  })

  test('(j) after the first sync the app works offline – list, detail, edits queued – and syncs on reconnect', async ({
    browser,
  }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    const id = await createNote(ana.page, 'Offline reading')
    await expect(ben.page.getByRole('link', { name: 'Offline reading', exact: true })).toBeVisible()
    await ben.page.waitForFunction(() => navigator.serviceWorker.controller !== null, undefined, { timeout: 30_000 })

    await ben.context.setOffline(true)
    await ben.page.reload()
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
    await expect(indicator(ben.page)).toContainText('Offline')
    await ben.page.getByRole('link', { name: 'Offline reading', exact: true }).click()
    await expect(ben.page.getByRole('heading', { level: 1, name: 'Offline reading' })).toBeVisible()
    await editTitle(ben.page, 'Edited offline')
    await ben.page.getByRole('button', { name: 'Pin', exact: true }).click()
    await expect(ben.page.getByTestId('sync-pending')).toHaveText('1')
    await expect(ben.page.getByText('Not synced yet')).toBeVisible()
    await createNoteOffline(ben.page, 'Written offline')
    await expect(ben.page.getByTestId('sync-pending')).toHaveText('3')
    expect(await localRow(ben.page, 'items', id)).toMatchObject({ title: 'Edited offline', pinned: true })

    await ben.context.setOffline(false)
    await expect(ben.page.getByTestId('sync-pending')).toHaveCount(0)
    await expect(indicator(ben.page)).toContainText('Live')
    await notesLink(ana.page).click()
    await expect(ana.page.getByRole('link', { name: 'Edited offline', exact: true })).toBeVisible()
    await expect(ana.page.getByRole('link', { name: 'Written offline', exact: true })).toBeVisible()
    expectNoProblems(ana, ben)
  })

  test('(k) the sync indicator: Live, Offline with the pending count, a conflict count, Live again', async ({
    browser,
  }) => {
    const [anaUser, benUser] = team.users
    const ana = await device(browser, anaUser!)
    const ben = await device(browser, benUser!)
    const id = await createNote(ana.page, 'Indicator')
    await openNote(ben.page, 'Indicator')
    await expect(indicator(ben.page)).toHaveAttribute('aria-label', 'Sync: Live')

    await ben.context.setOffline(true)
    await expect(indicator(ben.page)).toHaveAttribute('aria-label', 'Sync: Offline')
    await editTitle(ben.page, 'Indicator, offline')
    await expect(indicator(ben.page)).toHaveAttribute('aria-label', 'Sync: Offline, 1 pending')
    await editTitle(ana.page, 'Indicator, online')
    await sent(ana.page)
    await ben.context.setOffline(false)
    await expect(indicator(ben.page)).toHaveAttribute('aria-label', 'Sync: Live, 1 conflict')
    await expect(indicator(ben.page)).toHaveClass(/conflicts/)
    await resolve(ben.page, id, 'Keep theirs', 'Their version is kept')
    await expect(indicator(ben.page)).toHaveAttribute('aria-label', 'Sync: Live')
    expectNoProblems(ana, ben)
  })
})

/** New note while offline: created locally, its page opens; it waits in the outbox. */
async function createNoteOffline(page: Page, title: string) {
  await notesLink(page).click()
  await page.getByRole('button', { name: 'New note' }).click()
  const dialog = page.getByRole('dialog', { name: 'New note' })
  await dialog.getByLabel('Title').fill(title)
  await dialog.getByRole('button', { name: 'Create note' }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await expect(page.getByText('Assigned at the first sync')).toBeVisible()
}
