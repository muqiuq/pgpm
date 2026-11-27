import type { ConflictEntry, Resolved } from './conflicts'
import type { PgpmDb } from './db'
import type { Repo, RepoTransaction } from './repo'

/**
 * The decisions of the conflict dialog (docs/03 §11.8, docs/01 §14.4, data model §14.2):
 *
 * - **Keep mine** → a new patch `{field: {old: theirs, new: mine}}` (if theirs changed again meanwhile, it conflicts
 *   again and the dialog shows the newer value); **Keep theirs** → the entry goes (the row has the server's value
 *   already); **Edit** → a patch with the edited value, `old` = theirs.
 * - `deleted_by_other` → **Restore with my changes** (the trash endpoint, then the changes again on the restored row)
 *   or **Discard**; `changed_since_delete` → **Delete anyway** (`force: true`) or **Keep it**.
 *
 * Every decision is reported with the next sync (`resolved[]`), for the History.
 */
export interface ResolveDeps {
  db: () => PgpmDb
  repo: Repo
  /** `POST /api/v1/trash/{batchId}/restore`. */
  restoreBatch: (batchId: string) => Promise<unknown>
  /** The trash batch whose root is this row, when the conflict didn't name it. */
  findBatch: (table: string, rowId: string) => Promise<string | null>
  /** An HTTP sync now (the restored row comes with it). */
  syncNow: () => Promise<unknown>
  /** How long to wait for a restored row, in ms. */
  restoreWait?: number
}

export type Resolution = Resolved['resolution']

function values(conflict: ConflictEntry, side: 'mine' | 'theirs'): Record<string, unknown> {
  return Object.fromEntries(Object.entries(conflict.fields).map(([key, f]) => [key, f[side]]))
}

/** Inside a repository transaction: the conflict goes, and its resolution waits for the next sync. */
async function finish(db: PgpmDb, conflict: ConflictEntry, resolution: Resolution) {
  await db.conflicts.delete(conflict.id)
  const fields = Object.keys(conflict.fields)
  const reported: Resolved[] = (fields.length ? fields : [null]).map((field) => ({
    table: conflict.table,
    id: conflict.rowId,
    field,
    resolution,
  }))
  await db.setMeta('resolved', [...(await db.getMeta<Resolved[]>('resolved', [])), ...reported])
}

export function conflictActions(deps: ResolveDeps) {
  const inTransaction = (run: (tx: RepoTransaction, db: PgpmDb) => Promise<void>) =>
    deps.repo.transaction((tx) => run(tx, deps.db()))

  /** Keep theirs, Discard, Keep it: the server's state stays. */
  function keepTheirs(conflict: ConflictEntry) {
    return inTransaction((_, db) => finish(db, conflict, 'theirs'))
  }

  /** Edit: the edited values on top of theirs. */
  function edit(conflict: ConflictEntry, edited: Record<string, unknown>) {
    return inTransaction(async (tx, db) => {
      await tx.patch(conflict.table, conflict.rowId, edited, { old: values(conflict, 'theirs') })
      await finish(db, conflict, 'edited')
    })
  }

  /** Delete anyway: the delete again, forced. */
  function deleteAnyway(conflict: ConflictEntry) {
    return inTransaction(async (tx, db) => {
      await tx.remove(conflict.table, conflict.rowId, { force: true })
      await finish(db, conflict, 'mine')
    })
  }

  async function waitForRestore(conflict: ConflictEntry) {
    const end = Date.now() + (deps.restoreWait ?? 10_000)
    while (Date.now() < end) {
      const row = await deps.db().synced(conflict.table).get(conflict.rowId)
      if (row && row.deleted !== true) return
      await new Promise((r) => setTimeout(r, 100))
    }
    throw new Error('The restored object did not arrive yet; try again in a moment.')
  }

  /** Restore with my changes: the trash's restore (online), then the changes on the restored row. */
  async function restoreWithMine(conflict: ConflictEntry) {
    const batch = conflict.deleteBatchId ?? (await deps.findBatch(conflict.table, conflict.rowId))
    if (!batch) throw new Error('It is no longer in the trash.')
    await deps.restoreBatch(batch)
    await deps.syncNow()
    await waitForRestore(conflict)
    await inTransaction(async (tx, db) => {
      const mine = values(conflict, 'mine')
      if (Object.keys(mine).length) await tx.patch(conflict.table, conflict.rowId, mine)
      await finish(db, conflict, 'mine')
    })
  }

  /** Keep mine: per kind – a patch over theirs, the restore, or the forced delete. */
  async function keepMine(conflict: ConflictEntry) {
    if (conflict.kind === 'deleted_by_other') return restoreWithMine(conflict)
    if (conflict.kind === 'changed_since_delete') return deleteAnyway(conflict)
    return inTransaction(async (tx, db) => {
      await tx.patch(conflict.table, conflict.rowId, values(conflict, 'mine'), { old: values(conflict, 'theirs') })
      await finish(db, conflict, 'mine')
    })
  }

  /** For all: one after the other; answers the ones that failed (a restore offline, a row gone). */
  async function all(conflicts: readonly ConflictEntry[], side: 'mine' | 'theirs') {
    const failed: { conflict: ConflictEntry; error: unknown }[] = []
    for (const conflict of conflicts) {
      try {
        await (side === 'mine' ? keepMine(conflict) : keepTheirs(conflict))
      } catch (error) {
        failed.push({ conflict, error })
      }
    }
    return failed
  }

  return {
    keepMine,
    keepTheirs,
    edit,
    restoreWithMine,
    discard: keepTheirs,
    deleteAnyway,
    keepIt: keepTheirs,
    keepMineForAll: (conflicts: readonly ConflictEntry[]) => all(conflicts, 'mine'),
    keepTheirsForAll: (conflicts: readonly ConflictEntry[]) => all(conflicts, 'theirs'),
  }
}

export type ConflictActions = ReturnType<typeof conflictActions>
