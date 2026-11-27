import Dexie from 'dexie'
import { localDb, withoutLocal, type PgpmDb, type StoredRow } from './db'
import { applyPatch, coalesce, getPath, jsonEqual, type Changes, type OutboxEntry, type Row } from './outbox'
import type { LocalMeta } from './rows'
import { syncedColumns, tableDef, type TableDef } from './tables'
import { uuidv7 } from './uuid'

/**
 * The only way the app changes synced data (docs/03 §11.3, §12.5): each operation updates the local row and appends its
 * outbox entry **in one Dexie transaction**, coalescing it with the entries not sent yet (outbox.ts), then tells the
 * sync engine (a push 150 ms later while live). Reads go through `useLive` / Dexie directly.
 *
 *     await repo.create('items', { type: 'note', title })
 *     await repo.patch('items', id, { title: 'New', 'style.fill': '#fff' })      old values from the local row
 *     await repo.patch('items', id, { description: md }, { old: { description: base } })
 *     await repo.remove('items', id)
 *     await repo.transaction(async (tx) => { const note = await tx.create(…); await tx.create('item_shares', …) })
 */

/** A write the local data refuses: the row is gone, or the table can't be written (read-only). */
export class LocalWriteError extends Error {
  readonly code: 'not_found' | 'read_only' | 'unknown_table'

  constructor(code: LocalWriteError['code'], message: string) {
    super(message)
    this.name = 'LocalWriteError'
    this.code = code
  }
}

export interface RepoOptions {
  db?: () => PgpmDb
  /** The signed-in user: `createdBy`, an item's owner. */
  userId: () => string | null
  /** After a committed operation: the sync engine schedules a push. */
  changed?: () => void
}

export interface CreateOptions {
  id?: string
  batch?: string
}

export interface PatchOptions {
  /**
   * The values the change was made on, where they aren't the local row's: an editor's text when editing began
   * (docs/03 §18.5), *Keep mine*'s `theirs` (§11.8). A key listed here is pushed even if its new value equals the
   * local one.
   */
  old?: Record<string, unknown>
  batch?: string
}

export interface RemoveOptions {
  /** *Delete anyway* (docs/03 §11.8). */
  force?: boolean
  batch?: string
}

function definition(table: string, op: 'insert' | 'patch' | 'delete'): TableDef {
  const def = tableDef(table)
  if (!def) throw new LocalWriteError('unknown_table', `No synced table ${table}.`)
  if (def.writes === 'none' || (def.writes === 'insert' && op !== 'insert')) {
    throw new LocalWriteError('read_only', `${table} can't be changed here.`)
  }
  return def
}

/** The members of a new row that go to the server: not the synced columns, not the server-owned ones. */
function pushedRow(def: TableDef, row: Row): Row {
  const drop = new Set<string>([...syncedColumns, ...(def.serverOwned ?? [])])
  return Object.fromEntries(Object.entries(row).filter(([k, v]) => !drop.has(k) && v !== undefined))
}

/**
 * Appends an entry to the outbox, coalesced with the entries not sent yet (data model §14.1), inside the caller's
 * transaction. Answers the outbox entries of the row afterwards.
 */
export async function appendOutbox(db: PgpmDb, entry: OutboxEntry): Promise<OutboxEntry[]> {
  const all = await db.outbox.orderBy('seq').toArray()
  const unsent = all.filter((e) => !e.sentAt)
  const after = coalesce([...unsent, entry])
  const kept = new Set(after.map((e) => e.seq))
  for (const e of unsent) if (!kept.has(e.seq)) await db.outbox.delete(e.seq!)
  for (const e of after) {
    if (e.seq === undefined) {
      await db.outbox.add(e)
    } else {
      const before = unsent.find((u) => u.seq === e.seq)
      if (!jsonEqual(before, e)) await db.outbox.put(e)
    }
  }
  return db.outbox.where('[table+id]').equals([entry.table, entry.id]).toArray()
}

/** The bookkeeping of a stored row (a row from before bookkeeping counts as synced at its rev). */
export function metaOf(row: StoredRow): LocalMeta {
  return row._local ?? { serverRev: typeof row.rev === 'number' ? row.rev : null, pending: false }
}

/**
 * A helper's work inside a repository transaction, as a Dexie promise. Dexie keeps its transaction zone across a
 * native `await` only for one hop: an async helper that awaits another async helper last (or whose caller does)
 * loses it, the caller's next write lands outside the transaction and it commits early (PrematureCommitError). Helpers
 * that take a {@link RepoTransaction} answer through this (`return inTransaction(work())`); the transaction's own
 * operations do already.
 */
export function inTransaction<T>(work: Promise<T>): Promise<T> {
  return Dexie.Promise.resolve(work)
}

/** The operations of one transaction, sharing one outbox batch. */
export class RepoTransaction {
  readonly batch: string
  /** The transaction's database: reads, and the bytes of a new file (`blobs`, local/files.ts) – no other writes. */
  readonly db: PgpmDb
  readonly userId: string | null

  constructor(db: PgpmDb, batch: string, userId: string | null) {
    this.db = db
    this.batch = batch
    this.userId = userId
  }

  private entry(fields: Pick<OutboxEntry, 'table' | 'id' | 'op' | 'baseRev'> & Partial<OutboxEntry>): OutboxEntry {
    return {
      mutationId: uuidv7(),
      batch: this.batch,
      changes: null,
      row: null,
      clientChangedAt: new Date().toISOString(),
      ...fields,
    }
  }

  // The operations answer Dexie promises (inTransaction), so helpers ending with `await tx.create(…)` keep the zone.

  /** A new row: an id (v7) unless given, the table's defaults, the synced columns until the server sets them. */
  create<T extends Row>(table: string, values: Partial<T>, options: CreateOptions = {}): Promise<T & StoredRow> {
    return inTransaction(this.createRow<T>(table, values, options))
  }

  /**
   * Changes members of a row: `values` maps keys (columns or dotted jsonb members) to their new values; the old ones
   * come from the local row unless given. Keys whose value doesn't change are left out; nothing left, nothing done.
   */
  patch(table: string, id: string, values: Record<string, unknown>, options: PatchOptions = {}): Promise<void> {
    return inTransaction(this.patchRow(table, id, values, options))
  }

  /**
   * Deletes a row: a tombstone until the server confirms (it soft-deletes with its children in a delete batch). A row
   * the server never had, and nothing else refers to, simply goes.
   */
  remove(table: string, id: string, options: RemoveOptions = {}): Promise<void> {
    return inTransaction(this.removeRow(table, id, options))
  }

  private async createRow<T extends Row>(
    table: string,
    values: Partial<T>,
    options: CreateOptions,
  ): Promise<T & StoredRow> {
    const def = definition(table, 'insert')
    const id = options.id ?? (typeof values.id === 'string' ? values.id : uuidv7())
    const now = new Date().toISOString()
    const row: Row = {
      ...(def.defaults?.({ userId: this.userId }) ?? {}),
      ...values,
      id,
      seq: 0,
      rev: 0,
      deleted: false,
      deleteBatchId: null,
      createdAt: now,
      createdBy: this.userId,
      updatedAt: now,
      updatedBy: this.userId,
    }
    const entries = await appendOutbox(
      this.db,
      this.entry({ table, id, op: 'insert', baseRev: null, row: pushedRow(def, row) }),
    )
    const stored = { ...row, _local: { serverRev: null, pending: entries.length > 0, base: null } } as T & StoredRow
    await this.db.synced(table).put(stored)
    return stored
  }

  private async patchRow(
    table: string,
    id: string,
    values: Record<string, unknown>,
    options: PatchOptions,
  ): Promise<void> {
    definition(table, 'patch')
    const store = this.db.synced(table)
    const stored = await store.get(id)
    if (!stored || stored.deleted === true) throw new LocalWriteError('not_found', 'It was deleted meanwhile.')
    const row = withoutLocal(stored)
    const changes: Changes = {}
    for (const [key, value] of Object.entries(values)) {
      const given = options.old !== undefined && Object.hasOwn(options.old, key)
      const old = given ? options.old![key] : getPath(row, key)
      if (!given && jsonEqual(old, value)) continue
      changes[key] = { old, new: value }
    }
    if (!Object.keys(changes).length) return
    const meta = metaOf(stored)
    const base = meta.pending ? (meta.base ?? null) : meta.serverRev === null ? null : row
    const entries = await appendOutbox(
      this.db,
      this.entry({ table, id, op: 'patch', baseRev: meta.serverRev, changes }),
    )
    const pending = entries.length > 0
    const next = pending ? applyPatch(row, changes) : (base ?? applyPatch(row, changes))
    await store.put({ ...next, id, _local: { serverRev: meta.serverRev, pending, base: pending ? base : undefined } })
  }

  private async removeRow(table: string, id: string, options: RemoveOptions): Promise<void> {
    definition(table, 'delete')
    const store = this.db.synced(table)
    const stored = await store.get(id)
    if (!stored || (stored.deleted === true && !options.force)) return
    const row = withoutLocal(stored)
    const meta = metaOf(stored)
    const base = meta.pending ? (meta.base ?? null) : meta.serverRev === null ? null : row
    const entries = await appendOutbox(
      this.db,
      this.entry({
        table,
        id,
        op: 'delete',
        baseRev: meta.serverRev,
        ...(options.force ? { force: true } : {}),
      }),
    )
    if (!entries.length && meta.serverRev === null) {
      await store.delete(id)
      return
    }
    await store.put({
      ...row,
      id,
      deleted: true,
      _local: { serverRev: meta.serverRev, pending: entries.length > 0, base: entries.length ? base : undefined },
    })
  }
}

export class Repo {
  private readonly options: RepoOptions

  constructor(options: RepoOptions) {
    this.options = options
  }

  private db(): PgpmDb {
    return (this.options.db ?? localDb)()
  }

  /**
   * Runs several operations in one Dexie transaction and one outbox batch (a create dialog's item and its shares). The
   * sync engine hears of it once, after the commit.
   */
  async transaction<R>(run: (tx: RepoTransaction) => Promise<R>, options: { batch?: string } = {}): Promise<R> {
    const db = this.db()
    const tx = new RepoTransaction(db, options.batch ?? uuidv7(), this.options.userId())
    const result = await db.transaction('rw', [...db.syncedTables(), db.outbox, db.conflicts, db.meta, db.blobs], () =>
      run(tx),
    )
    this.options.changed?.()
    return result
  }

  create<T extends Row>(table: string, values: Partial<T>, options: CreateOptions = {}): Promise<T & StoredRow> {
    return this.transaction((tx) => tx.create<T>(table, values, options), { batch: options.batch })
  }

  patch(table: string, id: string, values: Record<string, unknown>, options: PatchOptions = {}): Promise<void> {
    return this.transaction((tx) => tx.patch(table, id, values, options), { batch: options.batch })
  }

  remove(table: string, id: string, options: RemoveOptions = {}): Promise<void> {
    return this.transaction((tx) => tx.remove(table, id, options), { batch: options.batch })
  }
}

/** Who the app's repository writes as, and whom it tells (the sync engine, boot.ts). */
const appContext: { userId: string | null; changed: (() => void) | null } = { userId: null, changed: null }

export function setRepoContext(context: { userId?: string | null; changed?: (() => void) | null }) {
  Object.assign(appContext, context)
}

/** The app's repository, on the app's database. */
export const repo = new Repo({
  userId: () => appContext.userId,
  changed: () => appContext.changed?.(),
})
