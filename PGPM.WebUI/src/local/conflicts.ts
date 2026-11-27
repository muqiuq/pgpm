import type { OutboxOp, Row } from './outbox'

/**
 * A change the server held back for the user to decide (data model §14.2, docs/03 §11.8). Kept in IndexedDB
 * `conflicts` until resolved; survives reloads, and a reset.
 */
export type ConflictKind = 'field' | 'deleted_by_other' | 'changed_since_delete'

export interface ConflictField {
  /** The value this device set. */
  mine: unknown
  /** The server's value now (null for a deleted row). */
  theirs: unknown
  /** The value this device's change was made on. */
  old?: unknown
  /** Who changed it last, and when (ISO 8601). */
  by: string | null
  at: string | null
}

export interface ConflictEntry {
  id: string
  table: string
  rowId: string
  /** Map elements are grouped per map (phase 5). */
  mapId: string | null
  kind: ConflictKind
  /** The colliding fields; for the delete kinds the fields of the change (if any). */
  fields: Record<string, ConflictField>
  /** The server rev when the conflict was reported. */
  baseRev: number | null
  createdAt: string
  /** What the dialog names the object by (its title then). */
  title?: string | null
  /** deleted_by_other: the delete batch that *Restore with my changes* restores. */
  deleteBatchId?: string | null
  /** The operation that met the conflict. */
  op?: OutboxOp
  /** deleted_by_other of an insert: the row this device wanted to add. */
  row?: Row | null
}

/** What the next sync reports as resolved (docs/03 §11.4 `resolved[]`). */
export interface Resolved {
  table: string
  id: string
  field: string | null
  resolution: 'mine' | 'theirs' | 'edited'
}
