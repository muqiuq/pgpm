import { describe, expect, it } from 'vitest'
import { describeDataSource } from './dataSourceBehaviour'
import { uuidv7 } from './ids'
import { createMemorySource } from './memorySource'
import { C1, seed } from './testFixtures'

describeDataSource('the in-memory data source', async (data) => {
  const source = createMemorySource(data)
  return {
    source,
    written: async () => source.log.value.map((m) => ({ batch: m.batch, table: m.table, op: m.op, id: m.id })),
    setAccess: async (access) => source.setAccess(access),
  }
})

describe('the in-memory data source', () => {
  it('logs every write with its batch, and publishes the read-only tables it is given', () => {
    const source = createMemorySource(seed())
    void source.patch('map_elements', C1, { x: { old: 0, new: 5 } })
    expect(source.log.value).toEqual([
      { batch: null, table: 'map_elements', op: 'patch', id: C1, changes: { x: { old: 0, new: 5 } } },
    ])
    source.put('items', { id: 'x', type: 'note', title: 'New' })
    expect(source.items.value.map((i) => i.id)).toContain('x')
    source.drop('items', 'x')
    expect(source.items.value.map((i) => i.id)).not.toContain('x')
  })
})

describe('uuidv7', () => {
  it('makes version 7 ids that sort in creation order, also within a millisecond', () => {
    const ids = Array.from({ length: 50 }, () => uuidv7(1_764_000_000_000))
    for (const id of ids) expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect([...ids].sort()).toEqual(ids)
    expect(new Set(ids).size).toBe(50)
  })
})
