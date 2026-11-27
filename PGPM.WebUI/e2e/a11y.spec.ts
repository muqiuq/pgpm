import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { createTeam, signInAs, signInAsAdmin, type Team } from './helpers'

/**
 * Accessibility (docs/05 §4, docs/03 §12.10): axe-core's WCAG 2.1 A and AA rules on the main pages – sign-in, the item
 * lists, an item's page and its create dialog, the maps list, a map with its side panel and library, the admin pages (System included) –
 * in the light and the dark theme, and the lists, an item and a map at a phone's width. A finding names the rule, its
 * impact and the first elements it hit. Contrast included: the status colours and badges are checked in both themes.
 */

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

/** The page as it settles: no spinner, transitions (dialogs, the side panel) over. */
async function settle(page: Page) {
  await expect(page.locator('.p-progressspinner')).toHaveCount(0)
  await page.waitForTimeout(400)
}

async function audit(page: Page, what: string) {
  await settle(page)
  const result = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const found = result.violations.map(
    (v) =>
      `${v.id} (${v.impact}) ${v.help}: ${v.nodes
        .slice(0, 4)
        .map((n) => n.target.join(' '))
        .join(' | ')}`,
  )
  expect(found, `axe on ${what}`).toEqual([])
}

let team: Team

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL, ['Ana'], {
    Notes: 'Create',
    Ideas: 'Create',
    Leads: 'Create',
    Projects: 'Create',
    Maps: 'Create',
  })
})

test.afterEach(async () => {
  await team.admin.dispose()
})

/** Signed in, the first sync done (Create is enabled only then). */
async function signedIn(page: Page, path: string) {
  await signInAs(page, team.users[0]!, path)
  await expect(page.getByTestId('sync-indicator')).toContainText('Live')
}

async function newIdea(page: Page, title: string) {
  await page.goto('/ideas')
  await page.getByRole('button', { name: 'New idea' }).click()
  const dialog = page.getByRole('dialog', { name: 'New idea' })
  await dialog.getByLabel('Title').fill(title)
  await dialog.getByRole('button', { name: 'Create idea' }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
}

async function newCanvas(page: Page, name: string) {
  await page.goto('/maps')
  await page.getByRole('button', { name: 'New map' }).click()
  await page.getByRole('menuitem', { name: 'Canvas map' }).click()
  const dialog = page.getByRole('dialog', { name: 'New canvas map' })
  await dialog.getByLabel('Name').fill(name)
  await dialog.getByRole('button', { name: 'Create map' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
}

/** A rectangle drawn with the toolbar: selected afterwards, so the side panel shows it. */
async function drawRectangle(page: Page) {
  await page.getByRole('button', { name: 'Rectangle' }).click()
  const box = (await page.getByTestId('map-canvas').locator('svg.surface').boundingBox())!
  await page.mouse.move(box.x + box.width / 2 - 60, box.y + box.height / 2 - 40)
  await page.mouse.down()
  await page.mouse.move(box.x + box.width / 2 + 60, box.y + box.height / 2 + 40, { steps: 4 })
  await page.mouse.up()
}

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`${colorScheme} theme`, () => {
    test.use({ colorScheme })

    test(`sign-in (${colorScheme})`, async ({ page }) => {
      await page.goto('/sign-in')
      await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
      await audit(page, 'the sign-in page')
    })

    test(`lists, an item, a map (${colorScheme})`, async ({ page }) => {
      await signedIn(page, '/ideas')
      for (const path of ['/ideas', '/notes', '/leads', '/projects', '/persons', '/maps', '/gates', '/trash']) {
        await page.goto(path)
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
        await audit(page, path)
      }

      await page.goto('/ideas')
      await page.getByRole('button', { name: 'New idea' }).click()
      await expect(page.getByRole('dialog', { name: 'New idea' })).toBeVisible()
      await audit(page, 'the New idea dialog')
      await page.keyboard.press('Escape')

      const title = `Accessible idea ${Date.now().toString(36)}`
      await newIdea(page, title)
      await audit(page, 'an idea’s Overview')
      const tabs = page.getByRole('navigation', { name: 'Idea', exact: true })
      for (const tab of ['Links', 'Maps', 'History']) {
        await tabs.getByRole('link', { name: tab, exact: true }).click()
        await audit(page, `an idea’s ${tab} tab`)
      }
      await page.goto('/ideas')
      await expect(page.getByRole('row', { name: new RegExp(title) })).toBeVisible()
      await audit(page, 'the ideas list with an idea')

      await newCanvas(page, `Accessible map ${Date.now().toString(36)}`)
      await audit(page, 'an empty canvas map')
      await drawRectangle(page)
      await expect(page.locator('.map-drawer')).toBeVisible()
      await audit(page, 'a map with its side panel')
      await page.keyboard.press('Escape')
      await page.getByRole('button', { name: 'Library' }).click()
      await expect(page.getByTestId('library')).toBeVisible()
      await audit(page, 'a map with its library')
    })

    test(`the admin pages (${colorScheme})`, async ({ page }) => {
      await signInAsAdmin(page)
      for (const path of [
        '/admin/users',
        '/admin/groups',
        '/admin/mcp',
        '/admin/statuses',
        '/admin/roles',
        '/admin/sections',
        '/admin/templates',
        '/admin/packages',
        '/admin/microsoft',
        '/admin/audit-log',
        '/admin/system',
        '/account',
        '/account/security',
      ]) {
        await page.goto(path)
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
        await audit(page, path)
      }
    })
  })
}

test.describe('phone (390 × 844)', () => {
  test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })

  test('lists, the navigation overlay, an item and a map fit a phone', async ({ page }) => {
    await signedIn(page, '/ideas')
    // Nothing scrolls sideways (docs/webui-migration-plan §5.3: down to 360 px).
    const sideways = () => page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
    for (const path of ['/ideas', '/notes', '/maps', '/persons']) {
      await page.goto(path)
      await audit(page, `${path} on a phone`)
      expect(await sideways(), `${path} scrolls sideways`).toBe(false)
    }

    await page.getByRole('button', { name: 'Open navigation' }).click()
    const nav = page.getByRole('complementary', { name: 'Navigation' })
    await expect(nav).toBeVisible()
    // The focus moves into the overlay; Escape closes it and gives the focus back to the menu button.
    await expect(nav.locator(':focus')).toHaveCount(1)
    await audit(page, 'the navigation overlay')
    await page.keyboard.press('Escape')
    await expect(nav).toBeHidden()
    await expect(page.getByRole('button', { name: 'Open navigation' })).toBeFocused()

    await page.getByRole('button', { name: 'Search' }).click()
    await expect(page.getByRole('combobox', { name: 'Search' })).toBeFocused()
    await page.keyboard.press('Escape')

    const title = `Phone idea ${Date.now().toString(36)}`
    await newIdea(page, title)
    await audit(page, 'an idea on a phone')
    expect(await sideways(), 'an idea scrolls sideways').toBe(false)

    await newCanvas(page, `Phone map ${Date.now().toString(36)}`)
    // The filter bar folds into a button; the library is a sheet over the map's bottom.
    await expect(page.getByRole('button', { name: 'Filter' })).toBeVisible()
    await page.getByRole('button', { name: 'Library' }).click()
    const library = page.getByTestId('library')
    const sheet = (await library.boundingBox())!
    expect(sheet.width).toBeGreaterThan(300)
    await audit(page, 'a map with its library on a phone')
    expect(await sideways(), 'a map scrolls sideways').toBe(false)
  })
})

test('the installed app’s shortcuts open the create dialog (`?new=1`)', async ({ page }) => {
  await signedIn(page, '/maps')
  const manifest = await (await page.request.get('/manifest.webmanifest')).json()
  expect((manifest.shortcuts as { url: string }[]).map((s) => s.url)).toEqual(['/notes?new=1', '/ideas?new=1', '/maps'])
  await page.goto('/notes?new=1')
  await expect(page.getByRole('dialog', { name: 'New note' })).toBeVisible()
  // The list's URL doesn't keep it: a reload doesn't open it again.
  expect(new URL(page.url()).searchParams.has('new')).toBe(false)
})
