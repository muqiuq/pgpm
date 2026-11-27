import { generateKeyBetween, generateNKeysBetween } from 'fractional-indexing'

/**
 * Fractional ordering (data model §10): `z` of map elements, `sort_order` of layers, milestones, DoD items and
 * assignments are keys of the npm package fractional-indexing (`a0`, `a0V`, `a1`, …). Reordering writes the moved rows
 * only; ties (two clients inserting at the same place offline) are broken by id. Keys compare by code unit – never with
 * `localeCompare`, which ignores case ("Zz" < "a0"). The server checks the format (PGPM.Lib/Helper/FractionalKey.cs).
 */

/** A row with an order key. */
export interface Keyed {
  id: string
  key: string
}

/** A key between `a` and `b` (null: the start, the end). Equal bounds have no key between them: an error. */
export function keyBetween(a: string | null, b: string | null): string {
  if (a !== null && b !== null && a >= b) throw new Error(`No key between ${a} and ${b}`)
  return generateKeyBetween(a, b)
}

/** `n` keys in order between `a` and `b`, evenly spread (shorter than `n` calls of keyBetween). */
export function keysBetween(a: string | null, b: string | null, n: number): string[] {
  if (a !== null && b !== null && a >= b) throw new Error(`No keys between ${a} and ${b}`)
  return generateNKeysBetween(a, b, n)
}

function compareText(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

/** The order of two rows: by key, then by id (both by code unit). */
export function compareKeyThenId(aKey: string, aId: string, bKey: string, bId: string): number {
  return compareText(aKey, bKey) || compareText(aId, bId)
}

/** A copy in order. */
export function sortByKey<T extends Keyed>(rows: readonly T[]): T[] {
  return [...rows].sort((a, b) => compareKeyThenId(a.key, a.id, b.key, b.id))
}

/**
 * New keys that put `desired` (all rows of one list, in their new order) in that order: rows in `moved` always get one,
 * the others keep theirs as long as it still fits (a tie or an older key out of place gets a new one too). The rows that
 * change, by id.
 */
export function keysForOrder(desired: readonly Keyed[], moved: ReadonlySet<string>): Map<string, string> {
  const changes = new Map<string, string>()
  let previous: string | null = null
  let i = 0
  while (i < desired.length) {
    const row = desired[i]!
    if (!moved.has(row.id) && (previous === null || row.key > previous)) {
      previous = row.key
      i++
      continue
    }
    // A run of rows that need a key: up to the next row that keeps its own.
    let j = i
    while (j < desired.length) {
      const next = desired[j]!
      if (!moved.has(next.id) && (previous === null || next.key > previous)) break
      j++
    }
    const keys = keysBetween(previous, j < desired.length ? desired[j]!.key : null, j - i)
    keys.forEach((key, k) => changes.set(desired[i + k]!.id, key))
    previous = keys[keys.length - 1]!
    i = j
  }
  return changes
}

type Reorder = (rows: readonly Keyed[], ids: Iterable<string>) => Map<string, string>

/** The rows in order with the selected ones moved by `arrange`; only the selected rows that pass another one move. */
function reorder(
  rows: readonly Keyed[],
  ids: Iterable<string>,
  arrange: (order: Keyed[], selected: Set<string>) => Keyed[],
) {
  const selected = new Set(ids)
  const order = sortByKey(rows)
  const desired = arrange(order, selected)
  const before = new Map(order.map((row, index) => [row.id, index]))
  // A selected row moves when an unselected one changed sides with it.
  const moved = new Set<string>()
  desired.forEach((row, index) => {
    if (!selected.has(row.id)) return
    const was = before.get(row.id)!
    const passed = desired.some((other, otherIndex) => {
      if (selected.has(other.id)) return false
      const belowNow = otherIndex < index
      const belowBefore = before.get(other.id)! < was
      return belowNow !== belowBefore
    })
    if (passed) moved.add(row.id)
  })
  return moved.size ? keysForOrder(desired, moved) : new Map<string, string>()
}

/** Bring to front: the selected rows above all others, in their order. */
export const toFront: Reorder = (rows, ids) =>
  reorder(rows, ids, (order, selected) => [
    ...order.filter((r) => !selected.has(r.id)),
    ...order.filter((r) => selected.has(r.id)),
  ])

/** Send to back: the selected rows below all others, in their order. */
export const toBack: Reorder = (rows, ids) =>
  reorder(rows, ids, (order, selected) => [
    ...order.filter((r) => selected.has(r.id)),
    ...order.filter((r) => !selected.has(r.id)),
  ])

/** Bring forward: each selected row (a run of them together) one place up, past the next unselected row. */
export const forward: Reorder = (rows, ids) =>
  reorder(rows, ids, (order, selected) => {
    const next = [...order]
    for (let i = next.length - 2; i >= 0; i--) {
      if (selected.has(next[i]!.id) && !selected.has(next[i + 1]!.id)) [next[i], next[i + 1]] = [next[i + 1]!, next[i]!]
    }
    return next
  })

/** Send backward: each selected row (a run of them together) one place down, past the previous unselected row. */
export const backward: Reorder = (rows, ids) =>
  reorder(rows, ids, (order, selected) => {
    const next = [...order]
    for (let i = 1; i < next.length; i++) {
      if (selected.has(next[i]!.id) && !selected.has(next[i - 1]!.id)) [next[i], next[i - 1]] = [next[i - 1]!, next[i]!]
    }
    return next
  })
