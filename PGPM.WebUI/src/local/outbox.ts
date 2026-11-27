/**
 * The outbox (data model §14.1, docs/03 §11.5, §11.7): the mutations the device hasn't pushed yet, and the pure rules
 * the local repository and the sync engine apply to them – coalescing, applying a patch to a row, rebasing pending
 * patches on a fresh server row. No storage here (Dexie comes with the sync core).
 */

/** A synced row as the client stores it: the server's columns (snake_case), jsonb members as nested objects. */
export type Row = Record<string, unknown>

export type OutboxOp = 'insert' | 'patch' | 'delete'

/** One changed key of a patch: the value the user saw, and the one they set. */
export interface FieldChange {
  old: unknown
  new: unknown
}

/** A patch's keys: columns (`title`) or members of a jsonb column (`style.fill`, `config.size`). */
export type Changes = Record<string, FieldChange>

export interface OutboxEntry {
  /** The store's key (autoIncrement); absent until stored. */
  seq?: number
  /** uuid v7: the server answers a known one with the stored result. */
  mutationId: string
  /** Entries that belong together (a create dialog, a template). */
  batch: string
  table: string
  id: string
  op: OutboxOp
  /** The server `rev` the change was made on; null for an insert. */
  baseRev: number | null
  /** patch: the changed keys. */
  changes: Changes | null
  /** insert: the complete row. */
  row: Row | null
  clientChangedAt: string
  /** delete: the user chose *Delete anyway* (docs/03 §11.8). */
  force?: boolean
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** Equality of JSON values: objects by their members (in any order), arrays by position. */
export function jsonEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => jsonEqual(v, b[i]))
  }
  if (!isObject(a) || !isObject(b)) return false
  const keys = Object.keys(a).filter((k) => a[k] !== undefined)
  const other = Object.keys(b).filter((k) => b[k] !== undefined)
  return keys.length === other.length && keys.every((k) => Object.hasOwn(b, k) && jsonEqual(a[k], b[k]))
}

function partsOf(path: string): string[] {
  const parts = path.split('.')
  if (parts.some((p) => p === '' || p === '__proto__')) throw new Error(`Not a patch key: ${path}`)
  return parts
}

/** The value at a dotted key (`style.fill`); undefined where there is none. */
export function getPath(row: Row, path: string): unknown {
  let value: unknown = row
  for (const part of partsOf(path)) {
    if (!isObject(value) || !Object.hasOwn(value, part)) return undefined
    value = value[part]
  }
  return value
}

function setPath(target: Record<string, unknown>, parts: string[], value: unknown): Record<string, unknown> {
  const [head, ...rest] = parts as [string, ...string[]]
  const copy = { ...target }
  if (rest.length) {
    const child = copy[head]
    copy[head] = setPath(isObject(child) ? child : {}, rest, value)
  } else if (value === undefined) {
    delete copy[head]
  } else {
    copy[head] = value
  }
  return copy
}

/**
 * The row with each change's new value set, a copy: dotted keys set a member of a jsonb column (creating the object when
 * there is none), `undefined` removes the member. The row itself is not changed.
 */
export function applyPatch<T extends Row>(row: T, changes: Changes): T {
  let result: Record<string, unknown> = row
  for (const [path, change] of Object.entries(changes)) result = setPath(result, partsOf(path), change.new)
  return result as T
}

/** The changes without the keys whose new value is the old one. */
function withoutNoOps(changes: Changes): Changes {
  return Object.fromEntries(Object.entries(changes).filter(([, c]) => !jsonEqual(c.old, c.new)))
}

/** `style` and `style.fill`: one is a member of the other. */
function overlap(a: Changes, b: Changes): boolean {
  const keys = Object.keys(b)
  return Object.keys(a).some((k) =>
    keys.some((other) => other !== k && (other.startsWith(`${k}.`) || k.startsWith(`${other}.`))),
  )
}

function contains(value: unknown, text: string): boolean {
  if (value === text) return true
  if (Array.isArray(value)) return value.some((v) => contains(v, text))
  return isObject(value) && Object.values(value).some((v) => contains(v, text))
}

/** The entry names the id somewhere in its values (a reference to that row). */
function mentions(entry: OutboxEntry, id: string): boolean {
  return contains(entry.changes, id) || contains(entry.row, id)
}

function sameRow(a: OutboxEntry, b: OutboxEntry): boolean {
  return a.table === b.table && a.id === b.id
}

function lastOfRow(entries: OutboxEntry[], entry: OutboxEntry): number {
  for (let i = entries.length - 1; i >= 0; i--) if (sameRow(entries[i]!, entry)) return i
  return -1
}

/**
 * The outbox with the entries of a row combined (data model §14.1). Only for entries not sent yet: a merged entry keeps
 * the first one's `mutationId`, `seq`, `batch` and `baseRev`, and takes the last one's `clientChangedAt`.
 *
 * - patch + patch of the same row: one patch, each key with the first `old` and the last `new`; a key changed back to
 *   its original value disappears, a patch without keys too.
 * - insert + patches: the insert, with the patches applied to its row.
 * - insert + delete of a row that was never synced: both dropped.
 * - patch + delete: the delete, with the first patch's `baseRev`; the patches are dropped.
 *
 * A later entry is only merged into an earlier one when no entry between them inserts a row it refers to, so that a
 * reference never arrives before its row; an insert is only dropped when no other entry refers to the row.
 */
export function coalesce(entries: readonly OutboxEntry[]): OutboxEntry[] {
  const out: OutboxEntry[] = []
  for (const entry of entries) {
    if (entry.op === 'delete') {
      deleteInto(out, entry)
      continue
    }
    const entryNow = entry.op === 'patch' && entry.changes ? { ...entry, changes: withoutNoOps(entry.changes) } : entry
    if (entryNow.op === 'patch' && !Object.keys(entryNow.changes ?? {}).length) continue
    const at = lastOfRow(out, entryNow)
    const previous = at < 0 ? undefined : out[at]!
    const blocked = out.slice(at + 1).some((between) => between.op === 'insert' && mentions(entryNow, between.id))
    if (!previous || blocked || entryNow.op !== 'patch' || overlap(previous.changes ?? {}, entryNow.changes!)) {
      out.push(entryNow)
    } else if (previous.op === 'insert' && previous.row) {
      out[at] = {
        ...previous,
        row: applyPatch(previous.row, entryNow.changes!),
        clientChangedAt: entryNow.clientChangedAt,
      }
    } else if (previous.op === 'patch' && previous.changes) {
      const merged: Changes = { ...previous.changes }
      for (const [key, change] of Object.entries(entryNow.changes!)) {
        merged[key] = { old: Object.hasOwn(merged, key) ? merged[key]!.old : change.old, new: change.new }
      }
      const changes = withoutNoOps(merged)
      if (Object.keys(changes).length) out[at] = { ...previous, changes, clientChangedAt: entryNow.clientChangedAt }
      else out.splice(at, 1)
    } else {
      out.push(entryNow)
    }
  }
  return out
}

/** A delete: the row's pending patches go, an insert of it nothing refers to goes with the delete. */
function deleteInto(out: OutboxEntry[], entry: OutboxEntry) {
  let baseRev = entry.baseRev
  let at = lastOfRow(out, entry)
  while (at >= 0 && out[at]!.op === 'patch') {
    baseRev = out[at]!.baseRev
    out.splice(at, 1)
    at = lastOfRow(out, entry)
  }
  const previous = at < 0 ? undefined : out[at]!
  if (previous?.op === 'insert' && !out.some((other, i) => i !== at && mentions(other, entry.id))) {
    out.splice(at, 1)
    return
  }
  out.push({ ...entry, baseRev })
}

/**
 * A fresh server row with the device's pending changes to it applied again (docs/03 §11.7: the local store holds the
 * server row with the outbox applied). Patches apply their new values in order; a pending delete marks the row deleted,
 * as the server's tombstone will; a pending insert is the row the server sent. A tombstone stays a tombstone – the
 * patches will meet it as a conflict (`deleted_by_other`).
 */
export function rebase<T extends Row & { id: string }>(incoming: T, pending: readonly OutboxEntry[]): T {
  if (incoming.deleted === true) return incoming
  let row = incoming
  for (const entry of pending) {
    if (entry.id !== incoming.id) continue
    if (entry.op === 'patch' && entry.changes) row = applyPatch(row, entry.changes)
    else if (entry.op === 'delete') row = { ...row, deleted: true }
  }
  return row
}
