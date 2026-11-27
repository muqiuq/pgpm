import { withoutLocal, type StoredRow } from '@/local/db'
import { jsonEqual, type Changes, type Row } from '@/local/outbox'
import { localKey } from '@/local/rows'

/**
 * Between the local database and the map UI (docs/03 §13.4): the device stores rows as the sync delivers them –
 * camelCase members (`layerId`), jsonb members as nested objects – while the map UI reads and writes snake_case columns
 * (`layer_id`, `style.fill`, types.ts). Only a row's own members are renamed; jsonb content (`settings.cardBadges`,
 * `matrix.filter.statusIds`) stays as it is, so a patch key renames its first segment only.
 */

/** `layerId` → `layer_id`. */
export function snakeKey(key: string): string {
  return key.replace(/[A-Z]/g, (c) => `_${c.toLowerCase()}`)
}

/** `layer_id` → `layerId`. */
export function camelKey(key: string): string {
  return key.replace(/_([a-z0-9])/g, (_, c: string) => c.toUpperCase())
}

/** A patch key: the column renamed, the jsonb path kept (`default_view.types` → `defaultView.types`). */
export function camelPath(path: string): string {
  const dot = path.indexOf('.')
  return dot < 0 ? camelKey(path) : camelKey(path.slice(0, dot)) + path.slice(dot)
}

/** A stored row as the map UI reads it: snake_case members, without the device's bookkeeping. */
export function toMapRow<T>(row: StoredRow | Row): T {
  const out: Row = {}
  for (const [key, value] of Object.entries(row)) {
    if (key === localKey) continue
    out[snakeKey(key)] = value
  }
  return out as T
}

/** A row of the map UI as the repository takes it: camelCase members, `undefined` ones left out. */
export function fromMapRow(row: Row): Row {
  const out: Row = {}
  for (const [key, value] of Object.entries(row)) {
    if (value === undefined || key === localKey) continue
    out[camelKey(key)] = value
  }
  return out
}

/** A map patch as the repository takes it: new values and the values the user saw, by camelCase key. */
export function repoPatch(changes: Changes): { values: Record<string, unknown>; old: Record<string, unknown> } {
  const values: Record<string, unknown> = {}
  const old: Record<string, unknown> = {}
  for (const [key, change] of Object.entries(changes)) {
    if (jsonEqual(change.old, change.new)) continue
    const path = camelPath(key)
    values[path] = change.new
    old[path] = change.old
  }
  return { values, old }
}

function pending(row: StoredRow): boolean {
  return (row as { _local?: { pending?: boolean } })._local?.pending === true
}

/**
 * Whether a stored row shows the same as before: the server's `seq` and `rev` (a re-stamp changes `seq` – a computed
 * `access` –, an accepted change both) and, while local changes are pending (they change neither), the content.
 */
export function sameStored(a: StoredRow, b: StoredRow): boolean {
  if (a === b) return true
  if (a.seq !== b.seq || a.rev !== b.rev || a.deleted !== b.deleted) return false
  if (!pending(a) && !pending(b)) return true
  return jsonEqual(withoutLocal(a), withoutLocal(b))
}

/**
 * Keeps the row objects of a live query that didn't change (the canvas memoises by identity, dataSource.ts): each
 * answer's rows are matched with the previous answer's by id, an unchanged one keeps its converted object, and an
 * answer that changed nothing answers the previous array itself.
 */
export class Reconciler<T> {
  private known = new Map<string, { raw: StoredRow; row: T }>()
  private last: readonly T[] = []
  private readonly convert: (raw: StoredRow) => T

  constructor(convert: (raw: StoredRow) => T) {
    this.convert = convert
  }

  next(raws: readonly StoredRow[]): readonly T[] {
    const known = new Map<string, { raw: StoredRow; row: T }>()
    let changed = raws.length !== this.last.length
    const out = raws.map((raw, i) => {
      const before = this.known.get(raw.id)
      const row = before && sameStored(before.raw, raw) ? before.row : this.convert(raw)
      known.set(raw.id, { raw, row })
      if (!changed && this.last[i] !== row) changed = true
      return row
    })
    this.known = known
    if (!changed) return this.last
    this.last = out
    return out
  }
}
