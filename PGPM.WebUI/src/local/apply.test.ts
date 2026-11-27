import { afterEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import { describeRow } from './apply'

describe('what a conflict is called (docs/03 §11.8)', () => {
  const db = memoryDb()
  afterEach(() => db.delete())

  it('names a row by its title, a field value by its item and field, a DoD item by its text', async () => {
    await db.synced('items').put({ id: 'i1', type: 'idea', title: 'Invoice bot' } as never)
    await db
      .synced('field_definitions')
      .put({ id: 'f1', key: 'feas_infra', label: 'We have the infrastructure' } as never)
    await db.synced('milestones').put({ id: 'm1', itemId: 'i1', title: 'G0' } as never)
    expect(await describeRow(db, 'items', { id: 'i1', title: 'Invoice bot' })).toBe('Invoice bot')
    expect(await describeRow(db, 'field_values', { id: 'v1', itemId: 'i1', fieldId: 'f1', value: 4 })).toBe(
      'Invoice bot · We have the infrastructure',
    )
    expect(await describeRow(db, 'field_values', { id: 'v2', itemId: 'gone', fieldId: 'f1', value: 4 })).toBe(
      'We have the infrastructure',
    )
    expect(await describeRow(db, 'dod_items', { id: 'd1', milestoneId: 'm1', text: ' Sponsor named ' })).toBe(
      'Sponsor named',
    )
    expect(await describeRow(db, 'assignments', { id: 'a1', itemId: 'i1', personId: 'p1' })).toBe('Invoice bot')
    expect(await describeRow(db, 'assignments', { id: 'a2', itemId: null, milestoneId: 'm1' })).toBe('G0')
    expect(await describeRow(db, 'map_elements', { id: 'e1', kind: 'rect' })).toBeNull()
  })
})
