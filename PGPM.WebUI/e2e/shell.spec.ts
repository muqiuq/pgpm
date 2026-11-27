import { expect, test, type Page } from '@playwright/test'
import { problems, signInAsAdmin } from './helpers'

/**
 * The walking skeleton (docs/05 §3 phase 0): the shell with the PGPM menu, installable as a PWA, and opening offline once
 * the service worker has cached it – signed in as the admin (phase 1), whose session the app keeps for offline starts.
 */

/** Waits until a service worker controls the page (the first one claims it: src/sw.ts). */
async function controlled(page: Page) {
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null, undefined, { timeout: 30_000 })
}

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page)
})

test('the shell shows the PGPM menu, folded administration and the placeholders', async ({ page }) => {
  const seen = problems(page)
  await page.goto('/')
  await expect(page).toHaveURL(/\/maps$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Maps' })).toBeVisible()
  await expect(page.getByText('Coming in phase 5')).toBeVisible()
  await expect(page).toHaveTitle('Maps · PGPM')

  const nav = page.getByRole('navigation', { name: 'Navigation' }).or(page.locator('nav.drawer-nav'))
  for (const entry of ['Maps', 'Projects', 'Leads', 'Ideas', 'Notes', 'Trash', 'Persons']) {
    await expect(nav.getByRole('link', { name: entry, exact: true })).toBeVisible()
  }
  const administration = page.getByRole('button', { name: 'Administration' })
  await expect(administration).toHaveAttribute('aria-expanded', 'false')
  await expect(nav.getByRole('link', { name: 'Users', exact: true })).toHaveCount(0)
  await administration.click()
  await expect(administration).toHaveAttribute('aria-expanded', 'true')
  for (const entry of [
    'Users',
    'Groups & Rights',
    'MCP',
    'Statuses',
    'Sections & Fields',
    'Project Templates',
    'Configuration packages',
    'Microsoft integration',
    'Audit log',
    'System',
  ]) {
    await expect(nav.getByRole('link', { name: entry, exact: true })).toBeVisible()
  }

  await nav.getByRole('link', { name: 'Project Templates', exact: true }).click()
  await expect(page).toHaveURL(/\/admin\/templates$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Project Templates' })).toBeVisible()
  await expect(page.getByText('Coming in phase 4')).toBeVisible()

  // The app bar: search placeholder, sync indicator, theme toggle, the drawer's footer with the server's version.
  await expect(page.getByRole('textbox', { name: 'Search' })).toBeVisible()
  await expect(page.getByTestId('sync-indicator')).toContainText('Live')
  await page.getByRole('button', { name: 'Dark theme' }).click()
  await expect(page.locator('html')).toHaveClass(/pgpm-dark/)
  await expect(page.locator('.drawer-footer')).toHaveText(/PGPM \d+\.\d+\.\d+/)

  expect(seen).toEqual([])
})

test('the app is installable: manifest, icons and a service worker', async ({ page, context }) => {
  await page.goto('/maps')
  const manifest = await (await page.request.get('/manifest.webmanifest')).json()
  expect(manifest).toMatchObject({
    id: '/',
    name: 'PGPM',
    start_url: '/',
    display: 'standalone',
    launch_handler: { client_mode: 'focus-existing' },
    theme_color: '#3b59a5',
  })
  for (const icon of manifest.icons as { src: string }[]) {
    expect((await page.request.get(icon.src)).ok(), icon.src).toBe(true)
  }
  await controlled(page)
  // Chromium's own verdict on installability (what decides whether it offers Install).
  const cdp = await context.newCDPSession(page)
  const { installabilityErrors } = await cdp.send('Page.getInstallabilityErrors')
  expect(installabilityErrors).toEqual([])
})

test('the shell opens offline after the first load', async ({ page, context }) => {
  const seen = problems(page)
  await page.goto('/ideas')
  await expect(page.getByRole('heading', { level: 1, name: 'Ideas' })).toBeVisible()
  await controlled(page)
  // Everything the worker precaches is in its cache before it controls the page; the fonts and icons with it.
  const cached = await page.evaluate(async () => {
    const names = await caches.keys()
    const all = await Promise.all(names.map(async (n) => (await (await caches.open(n)).keys()).map((r) => r.url)))
    return all.flat()
  })
  expect(cached.some((url) => /\/index\.html/.test(url))).toBe(true)
  expect(cached.some((url) => /\/assets\/.+\.woff2/.test(url))).toBe(true)

  await context.setOffline(true)
  try {
    // The server is out of reach for the page …
    const direct = await page.evaluate(() =>
      fetch('/healthz', { cache: 'no-store' }).then(
        (r) => r.status,
        () => 'unreachable',
      ),
    )
    expect(direct).toBe('unreachable')
    // … and for the service worker, so whatever opens next comes from its cache.
    const [worker] = context.serviceWorkers()
    expect(worker).toBeDefined()
    const fromWorker = await worker!.evaluate(() =>
      fetch('/healthz', { cache: 'no-store' }).then(
        (r) => r.status,
        () => 'unreachable',
      ),
    )
    expect(fromWorker).toBe('unreachable')

    // … and the shell still opens: a reload, and a deep link never visited before.
    const reloaded = await page.reload()
    expect(reloaded?.fromServiceWorker()).toBe(true)
    await expect(page.getByRole('heading', { level: 1, name: 'Ideas' })).toBeVisible()
    await expect(page.getByTestId('sync-indicator')).toContainText('Offline')
    await page.goto('/admin/audit-log')
    await expect(page.getByRole('heading', { level: 1, name: 'Audit log' })).toBeVisible()
    await expect(page.getByTestId('user-menu')).toBeVisible()
    await page.getByRole('link', { name: 'Notes', exact: true }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Notes' })).toBeVisible()
  } finally {
    await context.setOffline(false)
  }

  expect(seen).toEqual([])
})
