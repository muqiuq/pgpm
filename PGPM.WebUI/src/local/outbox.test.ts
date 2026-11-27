// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { applyPatch, coalesce, getPath, jsonEqual, rebase, type Changes, type OutboxEntry, type Row } from './outbox'

let counter = 0
/** An outbox entry with defaults: a patch of items/I1, made on rev 4. */
function entry(over: Partial<OutboxEntry> = {}): OutboxEntry {
  counter++
  return {
    seq: counter,
    mutationId: `m${counter}`,
    batch: `b${counter}`,
    table: 'items',
    id: 'I1',
    op: 'patch',
    baseRev: 4,
    changes: null,
    row: null,
    clientChangedAt: `2026-11-26T09:00:${String(counter % 60).padStart(2, '0')}Z`,
    ...over,
  }
}

const patch = (changes: Changes, over: Partial<OutboxEntry> = {}) => entry({ op: 'patch', changes, ...over })
const insert = (row: Row, over: Partial<OutboxEntry> = {}) =>
  entry({ op: 'insert', row, baseRev: null, id: String(row.id), ...over })
const del = (over: Partial<OutboxEntry> = {}) => entry({ op: 'delete', ...over })
const change = (old: unknown, next: unknown) => ({ old, new: next })

describe('applyPatch', () => {
  const row: Row = { id: 'E1', x: 10, style: { fill: '#fff', stroke: '#000' }, config: null, points: [[0, 0]] }

  it('sets columns and members of jsonb columns, as a copy', () => {
    const patched = applyPatch(row, {
      x: change(10, 20),
      'style.fill': change('#fff', '#fde68a'),
      'config.size': change(undefined, 'compact'),
    })
    expect(patched).toEqual({
      id: 'E1',
      x: 20,
      style: { fill: '#fde68a', stroke: '#000' },
      config: { size: 'compact' },
      points: [[0, 0]],
    })
    expect(row).toEqual({ id: 'E1', x: 10, style: { fill: '#fff', stroke: '#000' }, config: null, points: [[0, 0]] })
    expect(patched.points).toBe(row.points)
  })

  it('creates nested objects, replaces whole values and removes members set to undefined', () => {
    expect(applyPatch({ id: 'a' }, { 'config.view.zoom': change(undefined, 2) })).toEqual({
      id: 'a',
      config: { view: { zoom: 2 } },
    })
    expect(applyPatch(row, { style: change(row.style, { fill: '#000' }) }).style).toEqual({ fill: '#000' })
    expect(applyPatch(row, { 'style.stroke': change('#000', undefined) }).style).toEqual({ fill: '#fff' })
    expect(applyPatch(row, { x: change(10, null) }).x).toBeNull()
    expect(applyPatch(row, {})).toEqual(row)
  })

  it('refuses keys that are no paths', () => {
    expect(() => applyPatch(row, { 'style..fill': change(1, 2) })).toThrow()
    expect(() => applyPatch(row, { '__proto__.polluted': change(1, 2) })).toThrow()
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
  })

  it('reads dotted paths', () => {
    expect(getPath(row, 'style.fill')).toBe('#fff')
    expect(getPath(row, 'x')).toBe(10)
    expect(getPath(row, 'config.size')).toBeUndefined()
    expect(getPath(row, 'style.fill.deeper')).toBeUndefined()
    expect(getPath(row, 'toString')).toBeUndefined()
  })
})

describe('jsonEqual', () => {
  it('compares JSON values by content', () => {
    expect(jsonEqual({ a: 1, b: [1, { c: 2 }] }, { b: [1, { c: 2 }], a: 1 })).toBe(true)
    expect(jsonEqual([1, 2], [2, 1])).toBe(false)
    expect(jsonEqual({ a: 1 }, { a: 1, b: 2 })).toBe(false)
    expect(jsonEqual({ a: 1, b: undefined }, { a: 1 })).toBe(true)
    expect(jsonEqual(null, {})).toBe(false)
    expect(jsonEqual([], {})).toBe(false)
    expect(jsonEqual('1', 1)).toBe(false)
    expect(jsonEqual(null, undefined)).toBe(false)
  })
})

describe('coalesce: patches', () => {
  it('merges consecutive patches of a row: first old, last new, first baseRev and ids', () => {
    const first = patch({ title: change('Website', 'Website 2'), color: change('#fff', '#000') })
    const second = patch({ title: change('Website 2', 'Website relaunch') }, { baseRev: 5 })
    const result = coalesce([first, second])
    expect(result).toEqual([
      {
        ...first,
        changes: { title: change('Website', 'Website relaunch'), color: change('#fff', '#000') },
        clientChangedAt: second.clientChangedAt,
      },
    ])
  })

  it('drops a key changed back to its original value, and a patch with no key left', () => {
    const a = patch({ title: change('A', 'B'), color: change('#fff', '#000') })
    const b = patch({ title: change('B', 'A') })
    expect(coalesce([a, b])[0]!.changes).toEqual({ color: change('#fff', '#000') })
    const c = patch({ color: change('#000', '#fff') })
    expect(coalesce([a, b, c])).toEqual([])
    expect(coalesce([patch({ style: change({ fill: '#fff' }, { fill: '#fff' }) })])).toEqual([])
  })

  it('compares values as JSON, objects in any member order', () => {
    const a = patch({ points: change([[0, 0]], [[5, 5]]), config: change({ a: 1, b: 2 }, { a: 3 }) })
    const b = patch({ points: change([[5, 5]], [[0, 0]]), config: change({ a: 3 }, { b: 2, a: 1 }) })
    expect(coalesce([a, b])).toEqual([])
  })

  it('keeps patches of other rows apart and in place', () => {
    const a1 = patch({ x: change(0, 10) }, { table: 'map_elements', id: 'E1' })
    const b1 = patch({ x: change(0, 5) }, { table: 'map_elements', id: 'E2' })
    const a2 = patch({ x: change(10, 20) }, { table: 'map_elements', id: 'E1' })
    const sameIdOtherTable = patch({ x: change(1, 2) }, { table: 'map_layers', id: 'E1' })
    const result = coalesce([a1, b1, a2, sameIdOtherTable])
    expect(result.map((e) => [e.table, e.id, e.changes])).toEqual([
      ['map_elements', 'E1', { x: change(0, 20) }],
      ['map_elements', 'E2', { x: change(0, 5) }],
      ['map_layers', 'E1', { x: change(1, 2) }],
    ])
  })

  it('merges dotted keys of jsonb members like columns', () => {
    const a = patch({ 'style.fill': change('#fff', '#fde68a') }, { table: 'map_elements', id: 'E1' })
    const b = patch(
      { 'style.fill': change('#fde68a', '#000'), 'style.stroke': change(null, '#111') },
      { table: 'map_elements', id: 'E1' },
    )
    expect(coalesce([a, b])[0]!.changes).toEqual({
      'style.fill': change('#fff', '#000'),
      'style.stroke': change(null, '#111'),
    })
  })

  it("doesn't merge a key with a member of it (style and style.fill)", () => {
    const a = patch({ style: change({ fill: '#fff' }, { fill: '#000' }) })
    const b = patch({ 'style.fill': change('#000', '#111') })
    expect(coalesce([a, b])).toHaveLength(2)
    expect(coalesce([b, a])).toHaveLength(2)
    // The next one merges into the latest of the row.
    const c = patch({ 'style.fill': change('#111', '#222') })
    const result = coalesce([a, b, c])
    expect(result).toHaveLength(2)
    expect(result[1]!.changes).toEqual({ 'style.fill': change('#000', '#222') })
  })

  it("doesn't move a reference before the insert of the row it refers to", () => {
    const a = patch({ x: change(0, 1) }, { table: 'map_elements', id: 'L1' })
    const layer = insert({ id: 'LAYER2', map_id: 'M1', name: 'Notes' }, { table: 'map_layers' })
    const b = patch({ layer_id: change('LAYER1', 'LAYER2') }, { table: 'map_elements', id: 'L1' })
    const c = patch({ x: change(1, 2) }, { table: 'map_elements', id: 'L1' })
    const result = coalesce([a, layer, b, c])
    expect(result.map((e) => e.op)).toEqual(['patch', 'insert', 'patch'])
    expect(result[0]!.changes).toEqual({ x: change(0, 1) })
    expect(result[2]!.changes).toEqual({ layer_id: change('LAYER1', 'LAYER2'), x: change(1, 2) })
    // An insert of an unrelated row doesn't stand in the way.
    const other = insert({ id: 'N1', title: 'note' }, { table: 'items' })
    expect(coalesce([a, other, c]).map((e) => e.op)).toEqual(['patch', 'insert'])
  })

  it('leaves the entries it was given unchanged', () => {
    const a = patch({ title: change('A', 'B') })
    const b = patch({ title: change('B', 'C') })
    const copy = structuredClone([a, b])
    coalesce([a, b])
    expect([a, b]).toEqual(copy)
  })
})

describe('coalesce: inserts and deletes', () => {
  const row = { id: 'E9', map_id: 'M1', x: 0, y: 0, style: { fill: '#fff' } }

  it('applies patches to a pending insert', () => {
    const ins = insert(row, { table: 'map_elements' })
    const a = patch({ x: change(0, 40), 'style.fill': change('#fff', '#000') }, { table: 'map_elements', id: 'E9' })
    const b = patch({ y: change(0, 8) }, { table: 'map_elements', id: 'E9', baseRev: 1 })
    const result = coalesce([ins, a, b])
    expect(result).toEqual([
      { ...ins, row: { ...row, x: 40, y: 8, style: { fill: '#000' } }, clientChangedAt: b.clientChangedAt },
    ])
  })

  it('drops an insert and the delete of a row that was never synced, with the patches between', () => {
    const before = patch({ title: change('A', 'B') }, { id: 'I0' })
    const ins = insert({ id: 'I5', title: 'New' })
    const p = patch({ title: change('New', 'Newer') }, { id: 'I5' })
    const unrelated = insert({ id: 'I6', title: 'Other' })
    const d = del({ id: 'I5', baseRev: null })
    expect(coalesce([before, ins, p, unrelated, d])).toEqual([before, unrelated])
    expect(coalesce([ins, d])).toEqual([])
  })

  it('keeps both when another entry refers to the never-synced row', () => {
    const ins = insert({ id: 'I5', type: 'idea', title: 'New' })
    const value = insert({ id: 'FV1', item_id: 'I5', field_id: 'F1', value: 4 }, { table: 'field_values' })
    const line = patch({ start_element_id: change(null, 'I5') }, { table: 'map_elements', id: 'L1' })
    const d = del({ id: 'I5', baseRev: null })
    expect(coalesce([ins, value, d]).map((e) => [e.op, e.id])).toEqual([
      ['insert', 'I5'],
      ['insert', 'FV1'],
      ['delete', 'I5'],
    ])
    expect(coalesce([ins, line, d]).map((e) => [e.op, e.id])).toEqual([
      ['insert', 'I5'],
      ['patch', 'L1'],
      ['delete', 'I5'],
    ])
  })

  it('turns patch + delete into the delete, with the first baseRev', () => {
    const a = patch({ title: change('A', 'B') }, { baseRev: 4 })
    const b = patch({ title: change('B', 'C') }, { baseRev: 6 })
    const d = del({ baseRev: 7, force: true })
    expect(coalesce([a, b, d])).toEqual([{ ...d, baseRev: 4 }])
    // Patches kept apart by an insert between them go too.
    const layer = insert({ id: 'LAYER2' }, { table: 'map_layers' })
    const c = patch({ layer_id: change('LAYER1', 'LAYER2') }, { baseRev: 5 })
    expect(coalesce([a, layer, c, d])).toEqual([layer, { ...d, baseRev: 4 }])
  })

  it('keeps a delete without pending changes as it is', () => {
    const other = patch({ title: change('X', 'Y') }, { id: 'I2' })
    const d = del({ baseRev: 9 })
    expect(coalesce([other, d])).toEqual([other, d])
  })

  it("doesn't merge into a delete, an insert into a patch, or two inserts", () => {
    const d = del()
    const after = patch({ title: change('A', 'B') })
    expect(coalesce([d, after])).toEqual([d, after])
    const p = patch({ title: change('A', 'B') }, { id: 'X1' })
    const ins = insert({ id: 'X1' })
    expect(coalesce([p, ins])).toEqual([p, ins])
    const again = insert({ id: 'X1', title: 'again' })
    expect(coalesce([ins, again])).toEqual([ins, again])
  })

  it('coalesces a whole editing session on a map', () => {
    const m = { table: 'map_elements' }
    const session = [
      insert({ id: 'A', x: 0, y: 0, z: 'a0' }, m),
      patch({ x: change(0, 10) }, { ...m, id: 'A' }),
      patch({ x: change(10, 30), y: change(0, 5) }, { ...m, id: 'A' }),
      patch({ x: change(100, 120) }, { ...m, id: 'B', baseRev: 3 }),
      patch({ z: change('a1', 'a2') }, { ...m, id: 'B', baseRev: 3 }),
      patch({ x: change(120, 100) }, { ...m, id: 'B', baseRev: 3 }),
      insert({ id: 'C', x: 1 }, m),
      del({ ...m, id: 'C', baseRev: null }),
      patch({ text: change('a', 'b') }, { ...m, id: 'D', baseRev: 2 }),
      del({ ...m, id: 'D', baseRev: 2 }),
    ]
    expect(coalesce(session).map((e) => [e.op, e.id, e.op === 'insert' ? e.row : e.changes])).toEqual([
      ['insert', 'A', { id: 'A', x: 30, y: 5, z: 'a0' }],
      ['patch', 'B', { z: change('a1', 'a2') }],
      ['delete', 'D', null],
    ])
  })
})

describe('rebase', () => {
  const server = { id: 'I1', rev: 7, title: 'Website 2027', color: '#fff', style: { fill: '#fff', stroke: '#000' } }

  it('applies the pending patches of the row on the fresh server row', () => {
    const pending = [
      patch({ title: change('Website', 'Website relaunch') }),
      patch({ 'style.fill': change('#fff', '#fde68a') }),
      patch({ title: change('X', 'Y') }, { id: 'I2' }),
    ]
    expect(rebase(server, pending)).toEqual({
      ...server,
      title: 'Website relaunch',
      style: { fill: '#fde68a', stroke: '#000' },
    })
    expect(server.title).toBe('Website 2027')
  })

  it("keeps the server's values where nothing is pending", () => {
    expect(rebase(server, [])).toBe(server)
    expect(rebase(server, [patch({ color: change('#fff', '#000') }, { id: 'I2' })])).toEqual(server)
  })

  it('marks the row deleted for a pending delete, and leaves a tombstone a tombstone', () => {
    expect(rebase(server, [patch({ title: change('a', 'b') }), del()])).toEqual({
      ...server,
      title: 'b',
      deleted: true,
    })
    const tombstone = { id: 'I1', rev: 8, deleted: true }
    expect(rebase(tombstone, [patch({ title: change('a', 'b') })])).toBe(tombstone)
  })

  it('takes the server row for a pending insert of it (its result was lost)', () => {
    expect(rebase(server, [insert({ id: 'I1', title: 'mine' }), patch({ color: change('#fff', '#000') })])).toEqual({
      ...server,
      color: '#000',
    })
  })
})
