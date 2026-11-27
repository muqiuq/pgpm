// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  backward,
  compareKeyThenId,
  forward,
  keyBetween,
  keysBetween,
  keysForOrder,
  sortByKey,
  toBack,
  toFront,
  type Keyed,
} from './fractional'

/** The rows with the changed keys applied, in their new order: their ids. */
function after(rows: Keyed[], changes: Map<string, string>): string[] {
  return sortByKey(rows.map((r) => ({ ...r, key: changes.get(r.id) ?? r.key }))).map((r) => r.id)
}

/** Rows a, b, c, d, e with the keys a0 … a4. */
function five(): Keyed[] {
  return keysBetween(null, null, 5).map((key, i) => ({ id: 'abcde'[i]!, key }))
}

describe('fractional keys (data model §10)', () => {
  it('makes keys between, before and after', () => {
    expect(keyBetween(null, null)).toBe('a0')
    expect(keyBetween('a0', null)).toBe('a1')
    expect(keyBetween(null, 'a0')).toBe('Zz')
    expect(keyBetween('a1', 'a2')).toBe('a1V')
    expect(keysBetween(null, null, 3)).toEqual(['a0', 'a1', 'a2'])
    expect(keysBetween('a0', 'a1', 2)).toEqual(['a0G', 'a0V'])
    expect(keysBetween('a0', 'a1', 0)).toEqual([])
  })

  it('refuses bounds without room between them', () => {
    expect(() => keyBetween('a1', 'a1')).toThrow()
    expect(() => keyBetween('a2', 'a1')).toThrow()
    expect(() => keysBetween('a1', 'a1', 2)).toThrow()
  })

  it('keeps finding room when inserting at the same place again and again', () => {
    let low = 'a0'
    const high = 'a1'
    for (let i = 0; i < 200; i++) {
      const key = keyBetween(low, high)
      expect(key > low && key < high).toBe(true)
      low = key
    }
    let first = 'a0'
    for (let i = 0; i < 100; i++) {
      const key = keyBetween(null, first)
      expect(key < first).toBe(true)
      first = key
    }
  })

  it('orders by code unit, then by id', () => {
    expect(compareKeyThenId('Zz', 'b', 'a0', 'a')).toBe(-1)
    expect(compareKeyThenId('a1', 'a', 'a0V', 'b')).toBe(1)
    expect(compareKeyThenId('a0', 'a', 'a0', 'b')).toBe(-1)
    expect(compareKeyThenId('a0', 'b', 'a0', 'a')).toBe(1)
    expect(compareKeyThenId('a0', 'a', 'a0', 'a')).toBe(0)
    const rows = [
      { id: '2', key: 'a0' },
      { id: '1', key: 'a0' },
      { id: '3', key: 'Zz' },
    ]
    expect(sortByKey(rows).map((r) => r.id)).toEqual(['3', '1', '2'])
    expect(rows.map((r) => r.id)).toEqual(['2', '1', '3'])
  })
})

describe('keys for a new order', () => {
  it('rekeys the moved rows only', () => {
    const rows = five()
    const [a, b, c, d, e] = rows as [Keyed, Keyed, Keyed, Keyed, Keyed]
    const changes = keysForOrder([a, d, b, c, e], new Set(['d']))
    expect([...changes.keys()]).toEqual(['d'])
    expect(after(rows, changes)).toEqual(['a', 'd', 'b', 'c', 'e'])
  })

  it('rekeys a tie and a row out of place too', () => {
    const rows = [
      { id: 'a', key: 'a0' },
      { id: 'b', key: 'a1' },
      { id: 'c', key: 'a1' },
      { id: 'd', key: 'a2' },
    ]
    const changes = keysForOrder(rows, new Set())
    expect([...changes.keys()]).toEqual(['c'])
    expect(after(rows, changes)).toEqual(['a', 'b', 'c', 'd'])
    const reversed = keysForOrder([rows[3]!, rows[0]!], new Set())
    expect([...reversed.keys()]).toEqual(['a'])
  })

  it('rekeys a run of moved rows together, at the start and the end', () => {
    const rows = five()
    const [a, b, c, d, e] = rows as [Keyed, Keyed, Keyed, Keyed, Keyed]
    expect(after(rows, keysForOrder([d, e, a, b, c], new Set(['d', 'e'])))).toEqual(['d', 'e', 'a', 'b', 'c'])
    expect(after(rows, keysForOrder([c, d, e, a, b], new Set(['a', 'b'])))).toEqual(['c', 'd', 'e', 'a', 'b'])
    expect(keysForOrder([], new Set())).toEqual(new Map())
  })
})

describe('z-order: front, back, forward, backward', () => {
  it('brings to front and sends to back, keeping the selection in its order', () => {
    const rows = five()
    const front = toFront(rows, ['b', 'd'])
    expect([...front.keys()].sort()).toEqual(['b', 'd'])
    expect(after(rows, front)).toEqual(['a', 'c', 'e', 'b', 'd'])
    const back = toBack(rows, ['b', 'd'])
    expect(after(rows, back)).toEqual(['b', 'd', 'a', 'c', 'e'])
  })

  it('writes nothing for rows already there, and only the rows that pass another', () => {
    const rows = five()
    expect(toFront(rows, ['e']).size).toBe(0)
    expect(toFront(rows, ['d', 'e']).size).toBe(0)
    expect(toBack(rows, ['a', 'b']).size).toBe(0)
    expect(forward(rows, ['e']).size).toBe(0)
    expect(backward(rows, ['a']).size).toBe(0)
    expect(toFront(rows, []).size).toBe(0)
    // e is on top already: a goes just below it, the selection keeping its order.
    const front = toFront(rows, ['a', 'e'])
    expect([...front.keys()]).toEqual(['a'])
    expect(after(rows, front)).toEqual(['b', 'c', 'd', 'a', 'e'])
  })

  it('moves forward and backward by one place, a run of selected rows together', () => {
    const rows = five()
    expect(after(rows, forward(rows, ['b']))).toEqual(['a', 'c', 'b', 'd', 'e'])
    expect(after(rows, forward(rows, ['b', 'c']))).toEqual(['a', 'd', 'b', 'c', 'e'])
    expect(after(rows, forward(rows, ['a', 'c']))).toEqual(['b', 'a', 'd', 'c', 'e'])
    expect(after(rows, forward(rows, ['d', 'e']))).toEqual(['a', 'b', 'c', 'd', 'e'])
    expect(after(rows, backward(rows, ['d']))).toEqual(['a', 'b', 'd', 'c', 'e'])
    expect(after(rows, backward(rows, ['c', 'd']))).toEqual(['a', 'c', 'd', 'b', 'e'])
    expect(after(rows, backward(rows, ['a', 'e']))).toEqual(['a', 'b', 'c', 'e', 'd'])
  })

  it('works on rows in any order and with tied keys', () => {
    const rows = [
      { id: 'c', key: 'a1' },
      { id: 'a', key: 'a0' },
      { id: 'b', key: 'a0' },
    ]
    // In order: a, b (tie, by id), c.
    const changes = toFront(rows, ['a'])
    expect(after(rows, changes)).toEqual(['b', 'c', 'a'])
    expect(after(rows, forward(rows, ['a']))).toEqual(['b', 'a', 'c'])
    expect(after(rows, backward(rows, ['b']))).toEqual(['b', 'a', 'c'])
  })

  it('stays consistent over many random moves', () => {
    let seed = 7
    const random = () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646
    let rows = keysBetween(null, null, 12).map((key, i) => ({ id: `r${String(i).padStart(2, '0')}`, key }))
    let order = rows.map((r) => r.id)
    const ops = [toFront, toBack, forward, backward]
    for (let step = 0; step < 300; step++) {
      const ids = rows.filter(() => random() < 0.25).map((r) => r.id)
      const op = ops[Math.floor(random() * ops.length)]!
      const changes = op(rows, ids)
      rows = rows.map((r) => ({ ...r, key: changes.get(r.id) ?? r.key }))
      const next = sortByKey(rows).map((r) => r.id)
      // Only the selection changes places relative to the others; the others keep their order among themselves.
      const others = (list: string[]) => list.filter((id) => !ids.includes(id))
      expect(others(next)).toEqual(others(order))
      expect(new Set(rows.map((r) => r.key)).size).toBe(rows.length)
      order = next
    }
  })
})
