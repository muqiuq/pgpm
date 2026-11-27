import type { PgpmDb } from '@/local/db'
import type { Row } from '@/local/outbox'
import { fromMapRow } from '@/maps/mapRows'
import type { MemorySeed } from '@/maps/memorySource'

/**
 * A map's rows (the map tests' fixtures, maps/testFixtures.ts) as the sync would have stored them on a device:
 * camelCase, synced, nothing pending – so the local data source and the map pages read what the memory source holds.
 */
export async function storeMapSeed(db: PgpmDb, data: MemorySeed) {
  let seq = 0
  const put = async (table: string, rows: readonly Row[] | undefined) => {
    for (const row of rows ?? []) {
      await db.synced(table).put({
        createdAt: '2026-11-01T10:00:00Z',
        updatedAt: '2026-11-01T10:00:00Z',
        ...fromMapRow(row),
        id: row.id as string,
        seq: ++seq,
        rev: 1,
        deleted: false,
        _local: { serverRev: 1, pending: false },
      })
    }
  }
  await put('maps', [{ ...data.map, access: data.access ?? data.map.access ?? 'edit' }])
  await put('map_layers', data.layers)
  await put('map_elements', data.elements)
  await put('items', data.items)
  await put('statuses', data.statuses)
  await put('milestones', data.milestones)
  await put('dod_items', data.dodItems)
  await put('item_links', data.links)
  await put('assignments', data.assignments)
  await put('persons', data.persons)
  await put('field_definitions', data.fieldDefinitions)
  await put('field_values', data.fieldValues)
}
