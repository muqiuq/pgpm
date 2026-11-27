import Dexie from 'dexie'
import type { Eviction, FieldConflict, MutationResult } from '@/api/sync'
import type { ConflictEntry, ConflictField } from './conflicts'
import { withoutLocal, type PgpmDb, type StoredRow } from './db'
import { applyPatch, rebase, type Changes, type OutboxEntry, type Row } from './outbox'
import { metaOf } from './repo'
import { childrenOf, rootTable, syncedColumns, tableDef } from './tables'
import { uuidv7 } from './uuid'

/**
 * What the sync engine does to the local data (docs/03 §11.7): fresh server rows replace the stored ones and get the
 * pending outbox re-applied (rebase); evictions remove a root and everything below it; the push's results settle the
 * outbox entries. Each function runs inside the caller's Dexie transaction ({@link syncScope}).
 */

/** The stores a sync step writes. */
export function syncScope(db: PgpmDb) {
  return [...db.syncedTables(), db.outbox, db.conflicts, db.drafts, db.blobs, db.meta]
}

/** What a row is called in a toast or the conflict dialog. */
export function titleOf(row: Row | null | undefined): string | null {
  if (!row) return null
  for (const key of ['title', 'name', 'displayName', 'key']) {
    const value = row[key]
    if (typeof value === 'string' && value) return value
  }
  return null
}

/**
 * What the conflict dialog and the toasts name a row by: its own title, else what it belongs to – a field value is
 * "<item> · <field>", a DoD item its text, other rows of an item the item's title.
 */
export async function describeRow(db: PgpmDb, table: string, row: Row | null | undefined): Promise<string | null> {
  if (!row) return null
  const titled = async (t: string, id: unknown) =>
    typeof id === 'string' ? titleOf((await db.synced(t).get(id)) as Row | undefined) : null
  if (table === 'field_values') {
    const field = typeof row.fieldId === 'string' ? await db.synced('field_definitions').get(row.fieldId) : undefined
    const label = typeof field?.label === 'string' ? field.label : null
    return [await titled('items', row.itemId), label].filter(Boolean).join(' · ') || null
  }
  const own = titleOf(row)
  if (own) return own
  if (typeof row.text === 'string' && row.text.trim()) return row.text.trim().slice(0, 120)
  return (await titled('items', row.itemId)) ?? (await titled('milestones', row.milestoneId))
}

async function pendingOf(db: PgpmDb, table: string, id: string): Promise<OutboxEntry[]> {
  return db.outbox.where('[table+id]').equals([table, id]).sortBy('seq')
}

/** Stores the server's row with the pending entries re-applied; the server row is kept as the base while they wait. */
async function settle(db: PgpmDb, table: string, server: Row & { id: string }, serverRev: number | null) {
  const pending = await pendingOf(db, table, server.id)
  const local = rebase(server, pending)
  await db.synced(table).put({
    ...local,
    _local: { serverRev, pending: pending.length > 0, base: pending.length ? server : undefined },
  })
}

/** The row as the server has it, as far as this device knows (the base while entries wait, else the row). */
function serverSide(stored: StoredRow): Row {
  const meta = metaOf(stored)
  return meta.pending && meta.base ? meta.base : withoutLocal(stored)
}

/**
 * A fresh row from the server (pull page or live feed): it replaces the stored one when its rev is at least the
 * stored server rev (a re-stamp has the same rev and replaces too, updating `access`). A tombstone keeps what was known
 * of the row (its title for the dialogs) under the deleted flag. Answers whether it was stored.
 */
export async function applyServerRow(db: PgpmDb, table: string, incoming: Row): Promise<boolean> {
  const id = incoming.id
  if (typeof id !== 'string' || !tableDef(table)) return false
  const stored = await db.synced(table).get(id)
  const settled = settledRow(incoming, stored)
  if (!settled) return false
  await settle(db, table, settled.server, settled.rev)
  return true
}

/** What {@link applyServerRow} stores for a row (before the pending entries): the server's side and its rev; null: kept. */
function settledRow(
  incoming: Row,
  stored: StoredRow | undefined,
): { server: Row & { id: string }; rev: number | null } | null {
  const id = incoming.id as string
  const meta = stored ? metaOf(stored) : null
  const rev = typeof incoming.rev === 'number' ? incoming.rev : null
  if (meta?.serverRev !== null && meta?.serverRev !== undefined && rev !== null && rev < meta.serverRev) return null
  if (incoming.deleted === true) {
    if (!stored) return null
    return { server: { ...serverSide(stored), ...incoming, id }, rev }
  }
  return { server: { ...incoming, id }, rev }
}

/**
 * {@link applyServerRow} for a page's rows of one table, in three operations however many rows (docs/07 §2): the stored
 * rows read at once, the table's pending outbox entries once, the results written at once. A first sync of 10,000 rows
 * took one read, one outbox query and one write per row before.
 */
async function applyServerRows(db: PgpmDb, table: string, list: readonly Row[]): Promise<number> {
  const rows = list.filter((r): r is Row & { id: string } => typeof r.id === 'string')
  if (!rows.length) return 0
  const store = db.synced(table)
  const stored = await store.bulkGet(rows.map((r) => r.id))
  const waiting = await db.outbox.where('[table+id]').between([table, Dexie.minKey], [table, Dexie.maxKey]).toArray()
  const pendingById = new Map<string, OutboxEntry[]>()
  for (const entry of waiting.sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0))) {
    const list = pendingById.get(entry.id) ?? []
    list.push(entry)
    pendingById.set(entry.id, list)
  }
  const puts: StoredRow[] = []
  rows.forEach((incoming, i) => {
    const settled = settledRow(incoming, stored[i] as StoredRow | undefined)
    if (!settled) return
    const pending = pendingById.get(incoming.id) ?? []
    const local = rebase(settled.server, pending)
    puts.push({
      ...local,
      _local: {
        serverRev: settled.rev,
        pending: pending.length > 0,
        base: pending.length ? settled.server : undefined,
      },
    } as StoredRow)
  })
  await store.bulkPut(puts)
  return puts.length
}

/** A root that was evicted, for the toast. */
export interface EvictedRoot {
  kind: string
  id: string
  title: string | null
}

/** Removes a row and everything below it (registry parents), with its outbox entries, conflicts, drafts and bytes. */
export async function removeTree(db: PgpmDb, table: string, id: string): Promise<void> {
  for (const child of childrenOf(table)) {
    const ids = await db.synced(child.table).where(child.key).equals(id).primaryKeys()
    for (const childId of ids) await removeTree(db, child.table, childId)
  }
  await db.synced(table).delete(id)
  await db.outbox.where('[table+id]').equals([table, id]).delete()
  await db.conflicts.where('[table+rowId]').equals([table, id]).delete()
  await db.drafts.where('key').startsWith(`${table}:${id}:`).delete()
  if (tableDef(table)?.blobs) await db.blobs.delete(id)
}

/** Access to a root was lost (data model §11.4): it goes from the device with everything below it. */
export async function applyEviction(db: PgpmDb, eviction: Eviction): Promise<EvictedRoot | null> {
  const def = rootTable(eviction.kind)
  if (!def) return null
  const root = await db.synced(def.name).get(eviction.id)
  await removeTree(db, def.name, eviction.id)
  return root ? { kind: eviction.kind, id: eviction.id, title: titleOf(root) } : null
}

/**
 * A page of evictions and rows (docs/03 §11.4): evictions first, then the rows – the server pulls only what the user may
 * see now, so a row in the same page as its root's eviction means the access came back (a share added again).
 */
export async function applyChanges(
  db: PgpmDb,
  changes: Record<string, Row[]> | null | undefined,
  evictions: readonly Eviction[] | null | undefined,
): Promise<{ rows: number; evicted: EvictedRoot[] }> {
  const evicted: EvictedRoot[] = []
  for (const eviction of evictions ?? []) {
    const root = await applyEviction(db, eviction)
    if (root) evicted.push(root)
  }
  let rows = 0
  for (const [table, list] of Object.entries(changes ?? {})) {
    if (!tableDef(table)) continue
    rows += await applyServerRows(db, table, list)
  }
  // A root that came back in the same page was not lost after all.
  const back = await Promise.all(
    evicted.map(async (root) => !!(await db.synced(rootTable(root.kind)!.name).get(root.id))),
  )
  return { rows, evicted: evicted.filter((_, i) => !back[i]) }
}

/** The reset (docs/03 §11.7): every synced store emptied; the outbox, conflicts, drafts and files stay. */
export async function clearSyncedTables(db: PgpmDb): Promise<void> {
  for (const table of db.syncedTables()) await table.clear()
}

// --- results -------------------------------------------------------------------------------------------------------

/** What the results of one push did, for the state and the toasts. */
export interface ResultSummary {
  accepted: number
  conflicts: number
  rejected: { table: string; id: string; title: string | null; code: string; message: string }[]
}

function replaceDeep(value: unknown, from: string, to: string): unknown {
  if (value === from) return to
  if (Array.isArray(value)) return value.map((v) => replaceDeep(v, from, to))
  if (typeof value === 'object' && value !== null) {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, replaceDeep(v, from, to)]))
  }
  return value
}

/**
 * A duplicate of a unique pair was merged into an existing row (docs/03 §11.5): the local copy goes, and whatever names
 * it – rows below it, outbox entries, conflicts – names the existing one. That row arrives with the pull.
 */
async function rewriteId(db: PgpmDb, table: string, from: string, to: string) {
  await db.synced(table).delete(from)
  for (const child of childrenOf(table)) {
    const rows = await db.synced(child.table).where(child.key).equals(from).toArray()
    for (const row of rows) await db.synced(child.table).put(replaceDeep(row, from, to) as StoredRow)
  }
  const entries = await db.outbox.toArray()
  for (const entry of entries) {
    const next = replaceDeep(entry, from, to) as OutboxEntry
    if (JSON.stringify(next) !== JSON.stringify(entry)) await db.outbox.put(next)
  }
  await db.conflicts.where('[table+rowId]').equals([table, from]).modify({ rowId: to })
}

async function addConflict(db: PgpmDb, entry: Omit<ConflictEntry, 'id'>) {
  const existing = (await db.conflicts.where('[table+rowId]').equals([entry.table, entry.rowId]).toArray()).find(
    (c) => c.kind === entry.kind,
  )
  if (existing) {
    await db.conflicts.put({ ...existing, ...entry, id: existing.id, fields: { ...existing.fields, ...entry.fields } })
  } else {
    await db.conflicts.add({ ...entry, id: uuidv7() })
  }
}

/** The members of the stored row only the server sets: kept when an accepted insert becomes the base. */
function serverMembers(table: string, stored: StoredRow | undefined): Row {
  if (!stored) return {}
  const keys = new Set<string>([...syncedColumns, ...(tableDef(table)?.serverOwned ?? [])])
  return Object.fromEntries(Object.entries(withoutLocal(stored)).filter(([k]) => keys.has(k)))
}

function minusKeys(changes: Changes | null, keys: readonly string[]): Changes {
  return Object.fromEntries(Object.entries(changes ?? {}).filter(([k]) => !keys.includes(k)))
}

/**
 * Settles the entries a push sent, by their results (docs/03 §11.7): each answered entry leaves the outbox; accepted
 * changes become the base, conflicting keys take the server's value and become conflict entries (data model §14.2),
 * refused ones are undone (the server's `current`, or the local insert removed with its children). Entries without a
 * result stay for the next push.
 */
export async function applyResults(
  db: PgpmDb,
  sent: readonly OutboxEntry[],
  results: readonly MutationResult[],
  conflicts: readonly FieldConflict[],
): Promise<ResultSummary> {
  const summary: ResultSummary = { accepted: 0, conflicts: 0, rejected: [] }
  const now = new Date().toISOString()
  for (const result of results) {
    const entry = sent.find((e) => e.mutationId === result.mutationId)
    if (!entry) continue
    const still = await db.outbox.get(entry.seq!)
    if (!still) continue
    await db.outbox.delete(entry.seq!)
    const { table, id } = entry
    const stored = await db.synced(table).get(id)
    const meta = stored ? metaOf(stored) : null
    const base: Row | null = stored ? (meta!.pending ? (meta!.base ?? null) : withoutLocal(stored)) : null
    const rev = typeof result.rev === 'number' ? result.rev : null
    const newer = rev !== null && (meta?.serverRev === null || meta?.serverRev === undefined || rev > meta.serverRev)
    // Named as the server has it (the dialog shows both versions anyway); looked up only for what is reported.
    const title = () => describeRow(db, table, base ?? stored)

    if (result.status === 'accepted') {
      summary.accepted++
      if (result.mergedInto && result.mergedInto !== id) {
        await rewriteId(db, table, id, result.mergedInto)
        continue
      }
      if (!stored) continue
      let server: Row
      if (entry.op === 'insert') {
        server = newer
          ? { ...serverMembers(table, stored), ...(entry.row ?? {}), id, rev, seq: result.seq ?? 0, deleted: false }
          : (base ?? withoutLocal(stored))
      } else if (!base) {
        server = withoutLocal(stored)
      } else if (!newer) {
        server = base
      } else if (entry.op === 'patch') {
        server = { ...applyPatch(base, entry.changes ?? {}), rev, seq: result.seq ?? base.seq }
      } else {
        server = { ...base, deleted: true, rev, seq: result.seq ?? base.seq }
      }
      await settle(db, table, { ...server, id }, newer ? rev : (meta?.serverRev ?? null))
      continue
    }

    if (result.status === 'rejected') {
      summary.rejected.push({
        table,
        id,
        title: await title(),
        code: result.code ?? 'rejected',
        message: result.message ?? 'The server refused the change.',
      })
      const current = result.current
      if (current && typeof current === 'object') {
        const server = current.deleted === true && stored ? { ...serverSide(stored), ...current } : current
        const currentRev = typeof current.rev === 'number' ? current.rev : null
        await settle(db, table, { ...server, id }, currentRev)
      } else {
        // A refused insert, or a row the user can't see (any more): it goes with what hangs below it.
        await removeTree(db, table, id)
      }
      continue
    }

    // partial, or a conflict
    const current = result.current ?? null
    const kind = result.kind
    if (kind === 'deleted_by_other') {
      summary.conflicts++
      const fields: Record<string, ConflictField> = {}
      for (const [key, change] of Object.entries(entry.changes ?? {})) {
        fields[key] = { mine: change.new, old: change.old, theirs: null, by: null, at: null }
      }
      await addConflict(db, {
        table,
        rowId: id,
        mapId: (stored?.mapId as string | undefined) ?? null,
        kind,
        fields,
        baseRev: typeof current?.rev === 'number' ? current.rev : rev,
        createdAt: now,
        title: await title(),
        deleteBatchId: (current?.deleteBatchId as string | undefined) ?? null,
        op: entry.op,
        row: entry.op === 'insert' ? entry.row : null,
      })
      const known = stored ? serverSide(stored) : (entry.row ?? {})
      await settle(
        db,
        table,
        { ...known, ...(current ?? {}), id, deleted: true },
        typeof current?.rev === 'number' ? current.rev : (meta?.serverRev ?? null),
      )
      continue
    }
    if (kind === 'changed_since_delete') {
      summary.conflicts++
      await addConflict(db, {
        table,
        rowId: id,
        mapId: (current?.mapId as string | undefined) ?? (stored?.mapId as string | undefined) ?? null,
        kind,
        fields: {},
        baseRev: typeof current?.rev === 'number' ? current.rev : rev,
        createdAt: now,
        title: (await describeRow(db, table, current)) ?? (await title()),
        op: entry.op,
      })
      if (current) await settle(db, table, { ...current, id }, typeof current.rev === 'number' ? current.rev : null)
      continue
    }

    // Field conflicts (`partial`, or `conflict` with kind `field`): the other keys were applied, the colliding ones keep
    // the server's value. A `conflict` brings the server's row (`current`): nothing of the patch was applied.
    const reported = conflicts.filter((c) => c.mutationId === entry.mutationId)
    const keys = result.conflictFields ?? reported.map((c) => c.field)
    if (!stored) continue
    let server: Row =
      current && current.deleted !== true
        ? { ...current }
        : applyPatch(base ?? withoutLocal(stored), minusKeys(entry.changes, keys))
    const fields: Record<string, ConflictField> = {}
    let theirRev = rev
    for (const key of keys) {
      const change = entry.changes?.[key]
      const report = reported.find((c) => c.field === key)
      if (report) {
        server = applyPatch(server, { [key]: { old: null, new: report.theirs } })
        theirRev = Math.max(theirRev ?? 0, report.rev ?? 0)
      }
      fields[key] = {
        mine: change?.new,
        old: change?.old,
        theirs: report ? report.theirs : (base?.[key] ?? null),
        by: report?.by ?? null,
        at: report?.at ?? null,
      }
    }
    if (keys.length) {
      summary.conflicts++
      await addConflict(db, {
        table,
        rowId: id,
        mapId: (stored.mapId as string | undefined) ?? null,
        kind: 'field',
        fields,
        baseRev: theirRev,
        createdAt: now,
        title: await title(),
        op: entry.op,
      })
    } else {
      summary.accepted++
    }
    const settledRev = theirRev !== null && (meta?.serverRev ?? -1) < theirRev ? theirRev : (meta?.serverRev ?? null)
    await settle(db, table, { ...server, id, ...(settledRev !== null ? { rev: settledRev } : {}) }, settledRev)
  }
  return summary
}
