import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { ConflictEntry, Resolved } from './conflicts'
import type { PgpmDb } from './db'
import { Repo } from './repo'
import { conflictActions } from './resolve'

describe('resolving conflicts (docs/03 §11.8, data model §14.2)', () => {
  let db: PgpmDb
  let repo: Repo
  let actions: ReturnType<typeof conflictActions>
  const restoreBatch = vi.fn(async (_batch: string) => undefined)

  beforeEach(async () => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => 'me' })
    actions = conflictActions({
      db: () => db,
      repo,
      restoreBatch,
      findBatch: async () => null,
      syncNow: async () => undefined,
      restoreWait: 50,
    })
    await db.synced('items').put({ id: 'n1', type: 'note', title: 'Theirs', pinned: false, rev: 6, deleted: false })
  })
  afterEach(async () => {
    await db.delete()
  })

  const fieldConflict = (id = 'c1', rowId = 'n1'): ConflictEntry => ({
    id,
    table: 'items',
    rowId,
    mapId: null,
    kind: 'field',
    fields: { title: { mine: 'Mine', theirs: 'Theirs', old: 'Base', by: 'Ana', at: '2026-11-26T09:10:12Z' } },
    baseRev: 6,
    createdAt: '2026-11-26T09:12:03Z',
  })
  const resolved = () => db.getMeta<Resolved[]>('resolved', [])

  it('Keep mine: a patch from theirs to mine, the entry gone, the resolution queued – in one transaction', async () => {
    await db.conflicts.add(fieldConflict())
    await actions.keepMine((await db.conflicts.get('c1'))!)
    const [entry] = await db.outbox.toArray()
    expect(entry).toMatchObject({ op: 'patch', changes: { title: { old: 'Theirs', new: 'Mine' } } })
    expect(await db.synced('items').get('n1')).toMatchObject({ title: 'Mine' })
    expect(await db.conflicts.count()).toBe(0)
    expect(await resolved()).toEqual([{ table: 'items', id: 'n1', field: 'title', resolution: 'mine' }])
  })

  it('Keep theirs drops the entry only; Edit patches the edited value over theirs', async () => {
    await db.conflicts.bulkAdd([fieldConflict('c1'), fieldConflict('c2')])
    await actions.keepTheirs((await db.conflicts.get('c1'))!)
    expect(await db.outbox.count()).toBe(0)
    await actions.edit((await db.conflicts.get('c2'))!, { title: 'Both' })
    expect((await db.outbox.toArray())[0]!.changes).toEqual({ title: { old: 'Theirs', new: 'Both' } })
    expect((await resolved()).map((r) => r.resolution)).toEqual(['theirs', 'edited'])
  })

  it('Delete anyway pushes a forced delete; Keep it keeps the row', async () => {
    const deleteConflict: ConflictEntry = { ...fieldConflict(), kind: 'changed_since_delete', fields: {} }
    await db.conflicts.add(deleteConflict)
    await actions.deleteAnyway(deleteConflict)
    expect((await db.outbox.toArray())[0]).toMatchObject({ op: 'delete', force: true, baseRev: 6 })
    expect(await resolved()).toEqual([{ table: 'items', id: 'n1', field: null, resolution: 'mine' }])
  })

  it('Keep mine for all goes on after a failure and answers the ones that failed', async () => {
    restoreBatch.mockRejectedValueOnce(new Error('offline'))
    const gone: ConflictEntry = {
      ...fieldConflict('c2'),
      kind: 'deleted_by_other',
      deleteBatchId: 'b1',
      fields: { title: { mine: 'Mine', theirs: null, by: null, at: null } },
    }
    await db.conflicts.bulkAdd([gone, fieldConflict('c1')])
    const failed = await actions.keepMineForAll([gone, fieldConflict('c1')])
    expect(failed.map((f) => f.conflict.id)).toEqual(['c2'])
    expect(restoreBatch).toHaveBeenCalledWith('b1')
    expect((await db.conflicts.toArray()).map((c) => c.id)).toEqual(['c2'])
    expect(await db.outbox.count()).toBe(1)
  })
})
