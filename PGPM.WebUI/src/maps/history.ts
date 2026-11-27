import { computed, shallowRef } from 'vue'
import { applyPatch, getPath, jsonEqual, type Changes, type Row } from '@/local/outbox'
import type { MapDataSource, MapTable } from './dataSource'

/**
 * Undo and redo of a map's operations (docs/03 §13.4): a per-map command stack for the session. A command is a list of
 * changes `{table, id, before, after}` – a patch's changed keys (dotted for jsonb members) with their values before and
 * after, a created row (`before` null) or a removed one (`after` null); undo writes the inverse patches.
 *
 * Undoing a removal creates the row again **under a new id** (the server never takes an id twice) and remembers the
 * alias, so that every later step – and the references of lines and elements to it – follow the new row. The writes of
 * a step go to the data source in one batch: rows created first (lines last), then patches, then removals (lines first),
 * so that a reference never arrives before its row.
 */

export type ChangeOp = 'create' | 'patch' | 'remove'

export interface Change {
  table: MapTable
  id: string
  op: ChangeOp
  /** patch: the changed keys' values before; remove: the row. */
  before: Row | null
  /** patch: the changed keys' new values; create: the row. */
  after: Row | null
}

export interface Command {
  label: string
  changes: Change[]
}

/** What an operation writes: rows to create, keys to set (dotted, new values; `undefined` removes a member), rows to remove. */
export interface Tx {
  create(table: MapTable, row: Row): void
  patch(table: MapTable, id: string, values: Record<string, unknown>): void
  remove(table: MapTable, id: string): void
  /** The row as it is now, with this operation's writes so far. */
  row(table: MapTable, id: string): Row | undefined
}

/** Keys that hold another row's id: they follow aliases. */
const REFERENCES = ['layer_id', 'start_element_id', 'end_element_id'] as const

/** A planned write; `line`: of a line element (ordered after the rows it may refer to). */
type Write = { table: MapTable; id: string; line: boolean } & (
  { op: 'create'; row: Row } | { op: 'patch'; changes: Changes } | { op: 'remove' }
)

const isLineRow = (table: MapTable, row: Row | undefined) => table === 'map_elements' && row?.kind === 'line'

/** Plans one step's writes over the current rows, tracking its own writes, so that a second patch of a row sees the first. */
class Planner {
  readonly writes: Write[] = []
  readonly changes: Change[] = []
  private readonly overlay = new Map<string, Row | null>()

  constructor(private readonly rowOf: (table: MapTable, id: string) => Row | undefined) {}

  current(table: MapTable, id: string): Row | undefined {
    const key = `${table}:${id}`
    return this.overlay.has(key) ? (this.overlay.get(key) ?? undefined) : this.rowOf(table, id)
  }

  create(table: MapTable, row: Row) {
    const id = row.id as string
    this.overlay.set(`${table}:${id}`, row)
    this.writes.push({ op: 'create', table, id, row, line: isLineRow(table, row) })
    this.changes.push({ table, id, op: 'create', before: null, after: row })
  }

  patch(table: MapTable, id: string, values: Record<string, unknown>) {
    const row = this.current(table, id)
    if (!row) return
    const changes: Changes = {}
    const before: Row = {}
    const after: Row = {}
    for (const [key, value] of Object.entries(values)) {
      const old = getPath(row, key)
      if (jsonEqual(old, value) || (old === undefined && value === undefined)) continue
      changes[key] = { old, new: value }
      before[key] = old
      after[key] = value
    }
    if (!Object.keys(changes).length) return
    this.overlay.set(`${table}:${id}`, applyPatch(row, changes))
    this.writes.push({ op: 'patch', table, id, changes, line: false })
    this.changes.push({ table, id, op: 'patch', before, after })
  }

  remove(table: MapTable, id: string) {
    const row = this.current(table, id)
    if (!row) return
    this.overlay.set(`${table}:${id}`, null)
    this.writes.push({ op: 'remove', table, id, line: isLineRow(table, row) })
    this.changes.push({ table, id, op: 'remove', before: row, after: null })
  }
}

/** Creates (lines last), patches, removes (lines first): no reference before its row. */
function ordered(writes: Write[]): Write[] {
  return [
    ...writes.filter((w) => w.op === 'create' && !w.line),
    ...writes.filter((w) => w.op === 'create' && w.line),
    ...writes.filter((w) => w.op === 'patch'),
    ...writes.filter((w) => w.op === 'remove' && w.line),
    ...writes.filter((w) => w.op === 'remove' && !w.line),
  ]
}

export interface HistoryOptions {
  /** The current row of a table (maps: the map). */
  rowOf: (table: MapTable, id: string) => Row | undefined
  /** A write the data source refused. */
  onError?: (error: unknown) => void
  /** Commands kept (default 200). */
  limit?: number
}

export function createHistory(source: MapDataSource, options: HistoryOptions) {
  const limit = options.limit ?? 200
  const undoStack = shallowRef<Command[]>([])
  const redoStack = shallowRef<Command[]>([])
  const aliases = new Map<string, string>()

  /** The id a row has now: an undone removal created it again under a new id. */
  function resolve(id: string): string {
    let current = id
    for (let seen = 0; aliases.has(current) && seen < 1000; seen++) current = aliases.get(current)!
    return current
  }

  function resolveValues(values: Row): Row {
    const out: Row = { ...values }
    for (const key of REFERENCES) {
      if (typeof out[key] === 'string') out[key] = resolve(out[key] as string)
    }
    return out
  }

  function execute(planner: Planner) {
    const writes = ordered(planner.writes)
    if (!writes.length) return
    const report = (error: unknown) => options.onError?.(error)
    void source
      .batch(() => {
        for (const w of writes) {
          const done =
            w.op === 'create'
              ? source.create(w.table, w.row)
              : w.op === 'patch'
                ? source.patch(w.table, w.id, w.changes)
                : source.remove(w.table, w.id)
          done.catch(report)
        }
      })
      .catch(report)
  }

  function txOf(planner: Planner): Tx {
    return {
      create: (table, row) => planner.create(table, row),
      patch: (table, id, values) => planner.patch(table, id, values),
      remove: (table, id) => planner.remove(table, id),
      row: (table, id) => planner.current(table, id),
    }
  }

  /** Runs an operation: its writes go to the data source, the command onto the stack. Null: it changed nothing. */
  function run(label: string, fn: (tx: Tx) => void): Command | null {
    const planner = new Planner(options.rowOf)
    fn(txOf(planner))
    if (!planner.changes.length) return null
    const command = { label, changes: planner.changes }
    execute(planner)
    undoStack.value = [...undoStack.value, command].slice(-limit)
    redoStack.value = []
    return command
  }

  /** Recreates a row under a new id, the alias first so that the step's references follow it. */
  function recreate(planner: Planner, c: Change, row: Row, fresh: Map<string, string>) {
    const id = fresh.get(c.id)!
    planner.create(c.table, { ...resolveValues(row), id })
  }

  function step(command: Command, direction: 'undo' | 'redo'): string[] {
    const planner = new Planner(options.rowOf)
    const recreated = direction === 'undo' ? 'remove' : 'create'
    // New ids for the rows this step creates again, before anything refers to them.
    const fresh = new Map<string, string>()
    for (const c of command.changes) {
      if (c.op !== recreated) continue
      const was = resolve(c.id)
      const id = source.newId()
      aliases.set(was, id)
      fresh.set(c.id, id)
    }
    const changes = direction === 'undo' ? [...command.changes].reverse() : command.changes
    const touched = new Set<string>()
    for (const c of changes) {
      const id = resolve(c.id)
      if (c.op === 'patch') {
        const values = resolveValues((direction === 'undo' ? c.before : c.after) ?? {})
        planner.patch(c.table, id, values)
      } else if (c.op === recreated) {
        recreate(planner, c, (direction === 'undo' ? c.before : c.after)!, fresh)
      } else {
        planner.remove(c.table, id)
      }
      touched.add(id)
    }
    execute(planner)
    return [...touched]
  }

  /** Undoes the last command; the ids of the rows it touched (as they are now). */
  function undo(): string[] | null {
    const command = undoStack.value.at(-1)
    if (!command) return null
    undoStack.value = undoStack.value.slice(0, -1)
    const ids = step(command, 'undo')
    redoStack.value = [...redoStack.value, command]
    return ids
  }

  function redo(): string[] | null {
    const command = redoStack.value.at(-1)
    if (!command) return null
    redoStack.value = redoStack.value.slice(0, -1)
    const ids = step(command, 'redo')
    undoStack.value = [...undoStack.value, command].slice(-limit)
    return ids
  }

  function clear() {
    undoStack.value = []
    redoStack.value = []
    aliases.clear()
  }

  return {
    run,
    undo,
    redo,
    clear,
    resolve,
    canUndo: computed(() => undoStack.value.length > 0),
    canRedo: computed(() => redoStack.value.length > 0),
    undoLabel: computed(() => undoStack.value.at(-1)?.label ?? null),
    redoLabel: computed(() => redoStack.value.at(-1)?.label ?? null),
  }
}

export type MapHistory = ReturnType<typeof createHistory>
