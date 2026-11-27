import Dexie, { type DexieOptions, type Table } from 'dexie'
import { shallowRef } from 'vue'
import type { ConflictEntry } from './conflicts'
import type { OutboxEntry, Row } from './outbox'
import { localKey, type LocalMeta } from './rows'
import { schemaVersions, tables } from './tables'

/**
 * The local database `pgpm` (data model §14, docs/03 §11.3, PLM `db.js`): one store per synced table (tables.ts) plus
 * `outbox`, `conflicts`, `blobs`, `drafts` and `meta`. Additive versioned migrations only, from the registry.
 */

/** A stored synced row: the row with the outbox applied, and its bookkeeping in `_local`. */
export type StoredRow = Row & { id: string; _local?: LocalMeta }

export interface MetaRow {
  key: string
  value: unknown
}

/** A draft (docs/03 §11.11, §18.5): the editor's, by its key (`items:<id>:description`, `new:note`). */
export interface DraftRow {
  key: string
  revision: number | string | null
  base?: string
  text: string
  savedAt: string
}

/**
 * File bytes (docs/03 §10.2, local/files.ts), by the `files` row's id. The bytes as an ArrayBuffer: every browser's
 * IndexedDB keeps one (older Safari versions lost Blobs), and so does the tests' fake.
 */
export interface BlobRow {
  id: string
  bytes: ArrayBuffer
  /** The content type. */
  type: string
  size: number
  /** When stored here: the cache budget evicts the oldest downloads first. */
  cachedAt: string
  /** `local`: attached on this device; `download`: fetched from the server. */
  origin: 'local' | 'download'
  /** The server has the bytes (downloaded, or uploaded and committed): only then may the budget evict them. */
  uploaded: boolean
  /** The file's purpose (`attachment`, `cover`, …): prefetched images are evicted last. */
  purpose?: string
  /** Uploading: the next chunk to send, so that an interrupted upload resumes. */
  nextChunk?: number
  /** The server refused the upload for good (its reason); *Retry* clears it. */
  error?: string | null
}

/** The keys of `meta`. */
export type MetaKey = 'cursor' | 'accessEpoch' | 'clientId' | 'lastSyncAt' | 'appVersion' | 'userId' | 'resolved'

export class PgpmDb extends Dexie {
  declare outbox: Table<OutboxEntry, number>
  declare conflicts: Table<ConflictEntry, string>
  declare blobs: Table<BlobRow, string>
  declare drafts: Table<DraftRow, string>
  declare meta: Table<MetaRow, string>

  /** `options`: another IndexedDB (fake-indexeddb in the tests). */
  constructor(name = 'pgpm', options?: DexieOptions) {
    super(name, options)
    for (const { version, stores } of schemaVersions()) this.version(version).stores(stores)
  }

  /** A synced table's store (tables.ts). */
  synced(name: string): Table<StoredRow, string> {
    return this.table(name)
  }

  /** Every synced table's store. */
  syncedTables(): Table<StoredRow, string>[] {
    return tables.map((t) => this.synced(t.name))
  }

  async getMeta<T>(key: MetaKey, fallback: T): Promise<T> {
    const row = await this.meta.get(key)
    return row === undefined ? fallback : (row.value as T)
  }

  async setMeta(key: MetaKey, value: unknown): Promise<void> {
    await this.meta.put({ key, value })
  }
}

/** The row without its bookkeeping (PLM `withoutBookkeeping`): what the app shows and what leaves the device. */
export function withoutLocal<T extends Row>(row: T): Omit<T, '_local'> {
  if (!(localKey in row)) return row
  const copy: Row = { ...row }
  delete copy[localKey]
  return copy as Omit<T, '_local'>
}

/**
 * Set while another tab holds the database open with an older version, so this one can't upgrade it (PLM pitfall):
 * App.vue asks to close the other tab.
 */
export const databaseBlocked = shallowRef(false)

/** What happens when another tab upgrades or deletes the database: by default, this page reloads. */
let onVersionChange: () => void = () => {
  if (typeof location !== 'undefined') location.reload()
}

/** Replaces the reaction to a version change (tests). */
export function setVersionChangeHandler(handler: () => void) {
  onVersionChange = handler
}

/** A database with the blocked and versionchange reactions of docs/03 §11.3. */
export function openDatabase(name = 'pgpm', options?: DexieOptions): PgpmDb {
  const db = new PgpmDb(name, options)
  db.on('blocked', () => {
    databaseBlocked.value = true
  })
  db.on('ready', () => {
    databaseBlocked.value = false
  })
  db.on('versionchange', () => {
    // A newer version in another tab, or the database deleted there (sign-out): close, so it isn't held up, and
    // reload into the new state.
    db.close()
    onVersionChange()
    return false
  })
  return db
}

let current: PgpmDb | null = null

/** The app's database, opened on first use (again after a wipe). */
export function localDb(): PgpmDb {
  current ??= openDatabase()
  return current
}

/** Uses another database as the app's (tests); null: the next call opens `pgpm` again. */
export function useDatabase(db: PgpmDb | null) {
  current = db
}

const beforeWipe = new Set<() => unknown>()
const afterWipe = new Set<() => unknown>()

/**
 * Hooks around {@link wipeLocalData}: before (the sync engine stops, so nothing writes into the database while it
 * goes), after (views of the counts refresh). Answers the removal.
 */
export function onWipe(when: 'before' | 'after', hook: () => unknown): () => void {
  const hooks = when === 'before' ? beforeWipe : afterWipe
  hooks.add(hook)
  return () => hooks.delete(hook)
}

async function runHooks(hooks: Set<() => unknown>) {
  for (const hook of hooks) {
    try {
      await hook()
    } catch {
      // A hook's failure doesn't keep the data.
    }
  }
}

/** localStorage keys of local data (the session's offline copy is the session store's). */
const localStoragePrefixes = ['pgpm.draft.']

/**
 * Removes everything this device holds of the signed-in user (docs/03 §11.3, docs/01 §14.5): the database, cached files
 * in Cache Storage, draft copies in localStorage. The service worker and its precache (the app shell) stay, and so do the
 * device's preferences. Other tabs reload (versionchange).
 */
export async function wipeLocalData(): Promise<void> {
  await runHooks(beforeWipe)
  const db = current
  current = null
  try {
    if (db) await db.delete()
    else await Dexie.delete('pgpm')
  } catch {
    // No IndexedDB here (an old browser, a test without it): nothing was stored.
  }
  try {
    if (typeof caches !== 'undefined') {
      for (const name of await caches.keys()) {
        if (!name.startsWith('workbox-precache')) await caches.delete(name)
      }
    }
  } catch {
    // Cache Storage unavailable: nothing cached.
  }
  try {
    for (const key of Object.keys(localStorage)) {
      if (localStoragePrefixes.some((p) => key.startsWith(p))) localStorage.removeItem(key)
    }
  } catch {
    // Blocked storage: nothing kept there.
  }
  await runHooks(afterWipe)
}

/**
 * Makes the local data the given user's (docs/01 §14.5): if it belongs to someone else, it is wiped first. Answers
 * whether it wiped. Without IndexedDB, nothing to claim.
 */
export async function claimLocalData(userId: string, timeout = 5000): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    // An upgrade held up by another tab must not hold up the sign-in.
    const owner = await Promise.race([
      localDb().getMeta<string | null>('userId', null),
      new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new Error('timeout')), timeout))),
    ])
    clearTimeout(timer)
    if (owner === userId) return false
    if (owner) await wipeLocalData()
    await localDb().setMeta('userId', userId)
    return !!owner
  } catch {
    clearTimeout(timer)
    return false
  }
}
