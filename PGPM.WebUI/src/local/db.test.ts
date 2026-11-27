import { afterEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import { withoutLocal } from './db'
import { childrenOf, dbVersion, localStores, rootTable, schemaVersions, tableDef, tables } from './tables'
import { isUuidV7, uuidv7 } from './uuid'

describe('the table registry (data model §14)', () => {
  it('has the phase 2 tables with their wire names, parents indexed in the child', () => {
    expect(tables.map((t) => t.name)).toEqual(['items', 'item_shares', 'groups', 'persons', 'user_settings'])
    for (const table of tables) {
      expect(table.name).toMatch(/^[a-z_]+$/)
      for (const parent of table.parents) {
        expect(tableDef(parent.table), `${table.name} → ${parent.table}`).toBeDefined()
        expect(table.indexes, `${table.name}.${parent.key} is indexed`).toContain(parent.key)
      }
      expect(table.since).toBeLessThanOrEqual(dbVersion)
    }
    expect(childrenOf('items')).toEqual([{ table: 'item_shares', key: 'itemId' }])
    expect(rootTable('item')?.name).toBe('items')
    expect(tableDef('groups')?.writes).toBe('none')
    expect(tableDef('item_shares')?.writes).toBe('insert')
  })

  it('declares every store once per version, the local stores in version 1', () => {
    const versions = schemaVersions()
    expect(versions.map((v) => v.version)).toEqual(Array.from({ length: dbVersion }, (_, i) => i + 1))
    expect(versions[0]!.stores).toMatchObject(localStores)
    expect(versions[0]!.stores.items).toBe('id, type, statusId, ownerUserId')
    const declared = new Set(versions.flatMap((v) => Object.keys(v.stores)))
    for (const table of tables) expect(declared).toContain(table.name)
  })
})

describe('the local database', () => {
  const dbs: ReturnType<typeof memoryDb>[] = []
  const open = () => {
    const db = memoryDb()
    dbs.push(db)
    return db
  }
  afterEach(async () => {
    for (const db of dbs.splice(0)) await db.delete()
  })

  it('opens with the synced tables and the local stores, and keeps meta values', async () => {
    const db = open()
    await db.open()
    const names = db.tables.map((t) => t.name).sort()
    expect(names).toEqual([...tables.map((t) => t.name), 'outbox', 'conflicts', 'blobs', 'drafts', 'meta'].sort())
    expect(await db.getMeta('cursor', 0)).toBe(0)
    await db.setMeta('cursor', 42)
    expect(await db.getMeta('cursor', 0)).toBe(42)
    await db.outbox.add({
      mutationId: uuidv7(),
      batch: uuidv7(),
      table: 'items',
      id: 'x',
      op: 'delete',
      baseRev: 1,
      changes: null,
      row: null,
      clientChangedAt: new Date().toISOString(),
    })
    expect(await db.outbox.where('[table+id]').equals(['items', 'x']).count()).toBe(1)
  })

  it('strips the bookkeeping from a row', () => {
    const row = { id: 'a', title: 'T', _local: { serverRev: 1, pending: false } }
    expect(withoutLocal(row)).toEqual({ id: 'a', title: 'T' })
    expect(row._local).toBeDefined()
  })
})

describe('uuid v7', () => {
  it('has the version and variant and starts with the time', () => {
    const id = uuidv7(Date.UTC(2026, 10, 27))
    expect(isUuidV7(id)).toBe(true)
    expect(id.slice(0, 13)).toBe(uuidv7(Date.UTC(2026, 10, 27)).slice(0, 13))
    expect(uuidv7(1) < uuidv7(2 ** 40)).toBe(true)
    expect(new Set(Array.from({ length: 100 }, () => uuidv7())).size).toBe(100)
  })
})
