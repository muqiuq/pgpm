import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { PgpmDb } from './db'
import { inTransaction, LocalWriteError, Repo } from './repo'
import type { ItemRow } from './rows'

const me = '01970000-0000-7000-8000-000000000101'

describe('the repository (docs/03 §11.3): row and outbox entry in one transaction', () => {
  let db: PgpmDb
  let repo: Repo
  let changed: ReturnType<typeof vi.fn<() => void>>

  beforeEach(() => {
    db = memoryDb()
    changed = vi.fn<() => void>()
    repo = new Repo({ db: () => db, userId: () => me, changed })
  })
  afterEach(async () => {
    await db.delete()
  })

  const outbox = () => db.outbox.orderBy('seq').toArray()

  it('creates a row with a v7 id, defaults and bookkeeping, and an insert without the server columns', async () => {
    const note = await repo.create<ItemRow>('items', { type: 'note', title: 'First' })
    expect(note.id).toMatch(/^[0-9a-f-]{36}$/)
    const stored = await db.synced('items').get(note.id)
    expect(stored).toMatchObject({
      type: 'note',
      title: 'First',
      ownerUserId: me,
      number: null,
      pinned: false,
      access: 'manage',
      deleted: false,
      createdBy: me,
      _local: { serverRev: null, pending: true, base: null },
    })
    const [entry] = await outbox()
    expect(entry).toMatchObject({ table: 'items', id: note.id, op: 'insert', baseRev: null, changes: null })
    expect(entry!.row).toMatchObject({ id: note.id, type: 'note', title: 'First', ownerUserId: me })
    for (const key of ['seq', 'rev', 'deleted', 'createdAt', 'updatedBy', 'access', 'number', '_local']) {
      expect(entry!.row, key).not.toHaveProperty(key)
    }
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('patches with old values from the local row, and coalesces with the unsent insert', async () => {
    const note = await repo.create<ItemRow>('items', { type: 'note', title: 'First' })
    await repo.patch('items', note.id, { title: 'Second', pinned: true })
    const entries = await outbox()
    expect(entries).toHaveLength(1)
    expect(entries[0]!.op).toBe('insert')
    expect(entries[0]!.row).toMatchObject({ title: 'Second', pinned: true })
    expect(await db.synced('items').get(note.id)).toMatchObject({ title: 'Second', pinned: true })
  })

  it('keeps a sent entry apart, keeps the server row as the base, and drops a patch changed back', async () => {
    await db.synced('items').put({
      id: 'n1',
      type: 'note',
      title: 'Server',
      rev: 4,
      seq: 10,
      deleted: false,
      _local: { serverRev: 4, pending: false },
    })
    await repo.patch('items', 'n1', { title: 'Mine' })
    let entries = await outbox()
    expect(entries).toHaveLength(1)
    expect(entries[0]).toMatchObject({ op: 'patch', baseRev: 4, changes: { title: { old: 'Server', new: 'Mine' } } })
    const stored = await db.synced('items').get('n1')
    expect(stored).toMatchObject({ title: 'Mine', _local: { serverRev: 4, pending: true } })
    expect(stored!._local!.base).toMatchObject({ title: 'Server', rev: 4 })

    // Taken by a push: the next patch is an entry of its own.
    await db.outbox.update(entries[0]!.seq!, { sentAt: new Date().toISOString() })
    await repo.patch('items', 'n1', { title: 'Mine again' })
    entries = await outbox()
    expect(entries.map((e) => e.changes)).toEqual([
      { title: { old: 'Server', new: 'Mine' } },
      { title: { old: 'Mine', new: 'Mine again' } },
    ])

    // A patch changed back to its value: the unsent entry goes; the sent one keeps the row pending.
    await repo.patch('items', 'n1', { title: 'Mine' })
    entries = await outbox()
    expect(entries).toHaveLength(1)
    expect((await db.synced('items').get('n1'))!._local!.pending).toBe(true)
  })

  it('takes given old values (the editor base, Keep mine), and skips keys that do not change', async () => {
    await db.synced('items').put({ id: 'n1', type: 'note', title: 'T', description: 'theirs', rev: 2, deleted: false })
    await repo.patch('items', 'n1', { title: 'T' })
    expect(await outbox()).toHaveLength(0)
    await repo.patch('items', 'n1', { description: 'mine' }, { old: { description: 'base' } })
    expect((await outbox())[0]!.changes).toEqual({ description: { old: 'base', new: 'mine' } })
  })

  it('patches dotted members of jsonb columns', async () => {
    await db.synced('user_settings').put({ id: 's1', key: 'list:note', value: { sort: 'title' }, rev: 1 })
    await repo.patch('user_settings', 's1', { 'value.sort': '-title' })
    expect((await db.synced('user_settings').get('s1'))!.value).toEqual({ sort: '-title' })
    expect((await outbox())[0]!.changes).toEqual({ 'value.sort': { old: 'title', new: '-title' } })
  })

  it('removes: a tombstone for a synced row, nothing at all for a row the server never had', async () => {
    await db.synced('items').put({ id: 'n1', type: 'note', title: 'T', rev: 3, deleted: false })
    await repo.remove('items', 'n1')
    expect(await db.synced('items').get('n1')).toMatchObject({ deleted: true, _local: { pending: true } })
    expect((await outbox())[0]).toMatchObject({ op: 'delete', baseRev: 3 })

    const fresh = await repo.create<ItemRow>('items', { type: 'note', title: 'Gone soon' })
    await repo.remove('items', fresh.id)
    expect(await db.synced('items').get(fresh.id)).toBeUndefined()
    expect((await outbox()).filter((e) => e.id === fresh.id)).toEqual([])
  })

  it('marks a forced delete', async () => {
    await db.synced('items').put({ id: 'n1', type: 'note', title: 'T', rev: 3, deleted: false })
    await repo.remove('items', 'n1', { force: true })
    expect((await outbox())[0]).toMatchObject({ op: 'delete', force: true })
  })

  it('runs several operations in one batch, and the engine hears of it once', async () => {
    const { note } = await repo.transaction(async (tx) => {
      const note = await tx.create<ItemRow>('items', { type: 'note', title: 'Shared' })
      await tx.create('item_shares', { itemId: note.id, groupId: 'g1', access: 'edit' })
      await tx.create('item_shares', { itemId: note.id, groupId: 'g2', access: 'read' })
      return { note }
    })
    const entries = await outbox()
    expect(entries.map((e) => [e.table, e.op])).toEqual([
      ['items', 'insert'],
      ['item_shares', 'insert'],
      ['item_shares', 'insert'],
    ])
    expect(new Set(entries.map((e) => e.batch)).size).toBe(1)
    expect(await db.synced('item_shares').where('itemId').equals(note.id).count()).toBe(2)
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('writes nothing when one operation of the transaction fails', async () => {
    await expect(
      repo.transaction(async (tx) => {
        await tx.create<ItemRow>('items', { type: 'note', title: 'Half' })
        await tx.patch('items', 'missing', { title: 'x' })
      }),
    ).rejects.toBeInstanceOf(LocalWriteError)
    expect(await db.synced('items').count()).toBe(0)
    expect(await db.outbox.count()).toBe(0)
  })

  it('refuses read-only tables, and changes of insert-only ones', async () => {
    await expect(repo.create('groups', { name: 'x' })).rejects.toMatchObject({ code: 'read_only' })
    await db.synced('item_shares').put({ id: 's1', itemId: 'n1', groupId: 'g1', access: 'read', rev: 1 })
    await expect(repo.patch('item_shares', 's1', { access: 'edit' })).rejects.toMatchObject({ code: 'read_only' })
    await expect(repo.remove('item_shares', 's1')).rejects.toMatchObject({ code: 'read_only' })
    await expect(repo.create('nope', {})).rejects.toMatchObject({ code: 'unknown_table' })
  })

  it('keeps the transaction across helpers that end with a write (Dexie promises, not native ones)', async () => {
    // A helper whose last step is `await tx.create(…)`: a native promise there lost Dexie's zone, and the next write
    // of its caller committed the transaction early.
    const helper = async (tx: Parameters<Parameters<Repo['transaction']>[0]>[0], title: string) => {
      const row = await tx.create<ItemRow>('items', { type: 'note', title })
      return row
    }
    // A helper that awaits another helper last answers through inTransaction.
    const outer = (tx: Parameters<Parameters<Repo['transaction']>[0]>[0]) =>
      inTransaction(
        (async () => {
          const row = await helper(tx, 'One')
          return row
        })(),
      )
    await repo.transaction(async (tx) => {
      const one = await outer(tx)
      await helper(tx, 'Two')
      await tx.patch('items', one.id, { title: 'One, changed' })
      await tx.db.synced('items').count()
      await tx.create('items', { type: 'note', title: 'Three' })
    })
    expect((await db.synced('items').toArray()).map((r) => r.title).sort()).toEqual(['One, changed', 'Three', 'Two'])
    expect(new Set((await db.outbox.toArray()).map((e) => e.batch)).size).toBe(1)
  })
})
