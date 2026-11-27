import Dexie from 'dexie'
import { afterEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import { withoutLocal } from './db'
import { childrenOf, dbVersion, localStores, rootTable, schemaVersions, tableDef, tables } from './tables'
import { isUuidV7, uuidv7 } from './uuid'

describe('the table registry (data model §14)', () => {
  it('has every synced table of the data model with its wire name, parents indexed in the child', () => {
    expect(tables.map((t) => t.name).sort()).toEqual(
      [
        'items',
        'item_shares',
        'groups',
        'persons',
        'user_settings',
        'statuses',
        'assignments',
        'item_links',
        'files',
        'sections',
        'field_definitions',
        'field_values',
        'item_sections',
        'project_templates',
        'template_statuses',
        'template_sections',
        'template_field_defaults',
        'template_milestones',
        'saved_views',
        'assignment_roles',
        'milestones',
        'dod_items',
        'maps',
        'map_shares',
        'map_layers',
        'map_elements',
      ].sort(),
    )
    for (const table of tables) {
      expect(table.name).toMatch(/^[a-z_]+$/)
      for (const parent of table.parents) {
        expect(tableDef(parent.table), `${table.name} → ${parent.table}`).toBeDefined()
        expect(table.indexes, `${table.name}.${parent.key} is indexed`).toContain(parent.key)
      }
      expect(table.since).toBeLessThanOrEqual(dbVersion)
    }
    expect(childrenOf('items').map((c) => c.table)).toEqual([
      'item_shares',
      'assignments',
      'item_links',
      'files',
      'field_values',
      'item_sections',
      'milestones',
    ])
    expect(childrenOf('milestones').map((c) => c.table)).toEqual(['assignments', 'dod_items'])
    expect(rootTable('item')?.name).toBe('items')
    expect(rootTable('map')?.name).toBe('maps')
    for (const ro of ['groups', 'statuses', 'sections', 'field_definitions', 'project_templates', 'assignment_roles']) {
      expect(tableDef(ro)?.writes, ro).toBe('none')
    }
    expect(tableDef('item_shares')?.writes).toBe('insert')
    expect(tableDef('map_shares')?.writes).toBe('insert')
    expect(tableDef('files')?.blobs).toBe(true)
  })

  it('declares every store once per version, the local stores in version 1, each version with its indexes', () => {
    const versions = schemaVersions()
    expect(versions.map((v) => v.version)).toEqual(Array.from({ length: dbVersion }, (_, i) => i + 1))
    expect(versions[0]!.stores).toMatchObject(localStores)
    expect(versions[0]!.stores.items).toBe('id, type, statusId, ownerUserId')
    expect(versions[1]!.stores.items).toBe('id, type, statusId, ownerUserId, originItemId')
    expect(versions[1]!.stores.files).toBe('id, itemId, mapId')
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

  it('upgrades a version 1 database: the rows stay, the new index and tables are there', async () => {
    const name = `upgrade-${uuidv7()}`
    const { indexedDB, IDBKeyRange } = await import('fake-indexeddb')
    const old = new Dexie(name, { indexedDB, IDBKeyRange })
    old.version(1).stores(schemaVersions()[0]!.stores)
    await old.table('items').put({ id: 'p1', type: 'project', title: 'Old', originItemId: 'i1' })
    old.close()
    const db = memoryDb(name)
    dbs.push(db)
    expect(await db.synced('items').where('originItemId').equals('i1').primaryKeys()).toEqual(['p1'])
    expect(await db.synced('files').count()).toBe(0)
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
