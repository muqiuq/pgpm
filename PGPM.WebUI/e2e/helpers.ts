import { deflateSync } from 'node:zlib'
import { expect, request, type APIRequestContext, type Page } from '@playwright/test'

/** The first admin of the e2e stack (tools/e2e.sh passes Auth__InitialAdmin__Password) … */
export const initialAdminPassword = process.env.E2E_ADMIN_PASSWORD ?? ''

/** … and the password the first test changes it to; later tests sign in with it. */
export const adminPassword = 'e2e-admin-password-1'

/**
 * Script errors, CSP violations and Vue's warnings while a test runs: collected from the start, asserted at the end.
 * Expected failures don't count: a signed-out start's 401 of /auth/me, the gates' 403 before the password change, and
 * the offline test's unreachable server.
 */
export function problems(page: Page): string[] {
  const seen: string[] = []
  page.on('pageerror', (error) => seen.push(`pageerror: ${error.message}`))
  page.on('console', (message) => {
    const text = message.text()
    if (message.type() === 'error' || /\[Vue warn\]|Content Security Policy/.test(text)) {
      if (/Failed to load resource: net::ERR_INTERNET_DISCONNECTED/.test(text)) return
      // The live channel's reconnect attempts while offline (the sync falls back to polling).
      if (/WebSocket connection to '.*\/api\/v1\/sync\/live' failed/.test(text)) return
      if (/Failed to load resource: the server responded with a status of (401|403)/.test(text)) return
      seen.push(`${message.type()}: ${text}`)
    }
  })
  return seen
}

/**
 * Waits until the service worker controls the page (the first one claims it: src/sw.ts) – from then on every chunk of
 * the app is precached, so pages not opened yet load offline too, as for a user who has used the app once before.
 */
export async function offlineReady(page: Page) {
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null, undefined, { timeout: 30_000 })
}

/** The local sign-in form, filled and sent. */
export async function signIn(page: Page, username: string, password: string) {
  if (!/\/sign-in/.test(page.url())) await page.goto('/sign-in')
  await page.getByLabel('Username', { exact: true }).fill(username)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
}

/** The forced password change, and the 2FA suggestion after it put off. */
export async function changePassword(page: Page, current: string, next: string) {
  await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible()
  await page.getByLabel('Password you signed in with').fill(current)
  await page.getByLabel('New password', { exact: true }).fill(next)
  await page.getByLabel('New password again').fill(next)
  await page.getByRole('button', { name: 'Save and continue' }).click()
  await expect(page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })).toBeVisible()
  await page.getByRole('button', { name: 'Later' }).click()
}

/** Signed in as the admin, whichever test ran first; the 2FA suggestion put off. */
export async function signInAsAdmin(page: Page) {
  await page.goto('/sign-in')
  await signIn(page, 'admin', adminPassword)
  const suggestion = page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })
  const failed = page.getByRole('alert')
  await expect(suggestion.or(failed)).toBeVisible()
  if (await failed.isVisible()) {
    // The first test of a fresh stack: the initial password, changed now.
    await signIn(page, 'admin', initialAdminPassword)
    await changePassword(page, initialAdminPassword, adminPassword)
  } else {
    await page.getByRole('button', { name: 'Later' }).click()
  }
  await expect(page.getByTestId('user-menu')).toBeVisible()
}

export async function signOut(page: Page) {
  await page.getByTestId('user-menu').click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
}

/** Someone the sync tests sign in as: created by the admin through the API, the temporary password changed. */
export interface TestUser {
  id: string
  username: string
  password: string
  name: string
}

/**
 * The v1 API outside the browser (Playwright's request context): its own cookies, the CSRF header on unsafe calls
 * (the XSRF-TOKEN cookie echoed, docs/03 §4.3).
 */
export class Api {
  private constructor(readonly context: APIRequestContext) {}

  static async create(baseURL: string | undefined): Promise<Api> {
    return new Api(await request.newContext({ baseURL }))
  }

  /** The call's JSON (undefined for 204); an error with the status and body otherwise. */
  async call<T = unknown>(method: string, path: string, data?: unknown): Promise<T> {
    const headers: Record<string, string> = {}
    if (method !== 'GET') {
      const token = (await this.context.storageState()).cookies.find((c) => c.name === 'XSRF-TOKEN')?.value
      if (token) headers['X-XSRF-TOKEN'] = token
    }
    const response = await this.context.fetch(path, { method, headers, data })
    const text = await response.text()
    if (!response.ok()) throw new Error(`${method} ${path}: ${response.status()} ${text}`)
    return (text ? JSON.parse(text) : undefined) as T
  }

  signIn(username: string, password: string) {
    return this.call<{ next: string; me: { flags: { mustChangePassword: boolean } } }>('POST', '/api/v1/auth/login', {
      username,
      password,
    })
  }

  changePassword(current: string, next: string) {
    return this.call('POST', '/api/v1/account/password', { currentPassword: current, newPassword: next })
  }

  dispose() {
    return this.context.dispose()
  }
}

/** The admin through the API: the initial password changed first if no test did that yet (a fresh stack). */
export async function adminApi(baseURL: string | undefined): Promise<Api> {
  const api = await Api.create(baseURL)
  try {
    await api.signIn('admin', adminPassword)
    return api
  } catch {
    const login = await api.signIn('admin', initialAdminPassword)
    if (login.me.flags.mustChangePassword) await api.changePassword(initialAdminPassword, adminPassword)
    return api
  }
}

/** A group of its own with Notes: Create, and users in it (Ana, Ben, …): what one sync test works with. */
export interface Team {
  admin: Api
  group: { id: string; name: string }
  users: TestUser[]
}

export async function createTeam(
  baseURL: string | undefined,
  names: string[] = ['Ana', 'Ben'],
  rights: Record<string, string> = { Notes: 'Create' },
): Promise<Team> {
  const admin = await adminApi(baseURL)
  const stamp = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`
  const group = await admin.call<{ id: string; name: string }>('POST', '/api/v1/groups', {
    name: `Team ${stamp}`,
    rights,
  })
  const users: TestUser[] = []
  for (const name of names) {
    const username = `${name.toLowerCase()}-${stamp}`
    const created = await admin.call<{ user: { id: string }; temporaryPassword: { password: string } }>(
      'POST',
      '/api/v1/users',
      { username, displayName: `${name} ${stamp}`, groupIds: [group.id] },
    )
    const password = `e2e-${stamp}-${name.toLowerCase()}-pw`
    const own = await Api.create(baseURL)
    await own.signIn(username, created.temporaryPassword.password)
    await own.changePassword(created.temporaryPassword.password, password)
    await own.dispose()
    users.push({ id: created.user.id, username, password, name: `${name} ${stamp}` })
  }
  return { admin, group, users }
}

/** The sign-in form for a user, the 2FA suggestion put off, signed in. */
export async function signInAs(page: Page, user: TestUser, path = '/notes') {
  await page.goto(`/sign-in?returnUrl=${encodeURIComponent(path)}`)
  await signIn(page, user.username, user.password)
  const suggestion = page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })
  const menu = page.getByTestId('user-menu')
  await expect(suggestion.or(menu)).toBeVisible()
  if (await suggestion.isVisible()) await page.getByRole('button', { name: 'Later' }).click()
  await expect(menu).toBeVisible()
}

/** The rows of a store of the app's local database (IndexedDB `pgpm`, docs/03 §11.3), as the page has them now. */
export function localRows(page: Page, store: string): Promise<Record<string, unknown>[]> {
  return page.evaluate(
    (name) =>
      new Promise<Record<string, unknown>[]>((resolve, reject) => {
        const open = indexedDB.open('pgpm')
        open.onerror = () => reject(open.error)
        open.onsuccess = () => {
          const db = open.result
          const read = db.transaction(name, 'readonly').objectStore(name).getAll()
          read.onsuccess = () => {
            db.close()
            resolve(read.result as Record<string, unknown>[])
          }
          read.onerror = () => reject(read.error)
        }
      }),
    store,
  )
}

/** A real PNG (2 × 2) with a tEXt chunk: metadata the server strips at the commit (docs/03 §10.3). */
export function pngWithMetadata(): Buffer {
  const table = Array.from({ length: 256 }, (_, n) => {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    return c >>> 0
  })
  const crc = (bytes: Buffer) => {
    let c = 0xffffffff
    for (const b of bytes) c = table[(c ^ b) & 0xff]! ^ (c >>> 8)
    return (c ^ 0xffffffff) >>> 0
  }
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, 'latin1'), data])
    const length = Buffer.alloc(4)
    length.writeUInt32BE(data.length)
    const sum = Buffer.alloc(4)
    sum.writeUInt32BE(crc(body))
    return Buffer.concat([length, body, sum])
  }
  const header = Buffer.from([0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0])
  // Each row: the filter byte, then two RGB pixels.
  const pixels = Buffer.from([0, 220, 38, 38, 22, 163, 74, 0, 37, 99, 235, 234, 179, 8])
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('tEXt', Buffer.from('Author\0Somebody', 'latin1')),
    chunk('IDAT', deflateSync(pixels)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

/** A row of the sync's wire format (camelCase, as the devices store them). */
export type WireRow = Record<string, unknown> & { id: string }

/**
 * Every row the user's device would get: `POST /api/v1/sync` from 0 until nothing more is pending, by table – as one
 * device (a new device id after the first page would be reset to 0, and only the first page came back).
 */
export async function pullAll(api: Api): Promise<Record<string, WireRow[]>> {
  const tables: Record<string, WireRow[]> = {}
  const clientId = crypto.randomUUID()
  let since = 0
  let accessEpoch: number | undefined
  for (;;) {
    const answer = await api.call<{
      cursor: number
      more: boolean
      accessEpoch: number
      changes: Record<string, WireRow[]>
    }>('POST', '/api/v1/sync', {
      clientId,
      appVersion: 'e2e',
      since,
      accessEpoch,
      limit: 1000,
      mutations: [],
      resolved: [],
      pull: true,
    })
    accessEpoch = answer.accessEpoch
    for (const [table, rows] of Object.entries(answer.changes)) (tables[table] ??= []).push(...rows)
    since = answer.cursor
    if (!answer.more) return tables
  }
}

/** Inserts rows as one batch of the user's device (shares only go in their item's batch); answers the result codes. */
export async function pushInserts(api: Api, rows: { table: string; row: WireRow }[]): Promise<string[]> {
  const batch = crypto.randomUUID()
  const answer = await api.call<{ results: { status: string; code?: string }[] }>('POST', '/api/v1/sync', {
    clientId: crypto.randomUUID(),
    appVersion: 'e2e',
    since: 0,
    limit: 1,
    pull: false,
    resolved: [],
    mutations: rows.map(({ table, row }) => ({
      mutationId: crypto.randomUUID(),
      batch,
      table,
      id: row.id,
      op: 'insert',
      baseRev: null,
      changes: null,
      row,
      clientChangedAt: new Date().toISOString(),
    })),
  })
  return answer.results.map((r) => r.code ?? r.status)
}

/** A UUID v7 (the server takes no other id for a new row). */
export function uuidv7(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  const ms = Date.now()
  for (let i = 0; i < 6; i++) bytes[i] = Math.floor(ms / 2 ** (8 * (5 - i))) % 256
  bytes[6] = (bytes[6]! & 0x0f) | 0x70
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
