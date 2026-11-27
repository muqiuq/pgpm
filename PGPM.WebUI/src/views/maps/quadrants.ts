import { computed } from 'vue'
import { localDb } from '@/local/db'
import type { MapRecord } from '@/local/maps'
import type { ItemRow, ItemType } from '@/local/rows'
import { useLive } from '@/local/useLive'
import type { FieldDefinition } from '@/maps/cards'
import { toMapRow } from '@/maps/mapRows'
import type { Quadrant } from '@/maps/matrixLayout'
import type { ItemRow as MapItemRow } from '@/maps/types'
import type { ColumnDef } from '@/views/items/itemList'
import {
  quadrantMapsFor,
  quadrantsOf,
  type FieldDefinitionRecord,
  type ItemQuadrant,
  type QuadrantMap,
} from './mapList'

/**
 * The quadrant of an item for a matrix map (doc 01 §12.8 *as a list column and filter*): for an item type's list, one
 * column `quadrant:<map id>` per matrix map whose filter takes the type, with the quadrant's label, sorted by it; and
 * the lookup the list's quadrant filter uses. Computed on the device from the synced field values and shares, as the
 * map itself does (matrixLayout.ts `quadrantOf`).
 */
export const QUADRANT_COLUMN = 'quadrant:'

interface ValueRecord {
  itemId: string
  fieldId: string
  value: unknown
  deleted?: boolean
}
interface ShareRecord {
  itemId: string
  groupId: string
  deleted?: boolean
}

interface Lookups {
  maps: QuadrantMap[]
  defs: FieldDefinitionRecord[]
  values: ValueRecord[]
  shares: ShareRecord[]
}

export function useItemQuadrants(type: ItemType) {
  const lookups = useLive(
    async (): Promise<Lookups> => {
      const db = localDb()
      const maps = quadrantMapsFor((await db.synced('maps').toArray()) as unknown as MapRecord[], type)
      if (!maps.length) return { maps, defs: [], values: [], shares: [] }
      const alive = <T extends { deleted?: boolean }>(rows: T[]) => rows.filter((r) => !r.deleted)
      return {
        maps,
        defs: alive((await db.synced('field_definitions').toArray()) as unknown as FieldDefinitionRecord[]),
        values: alive((await db.synced('field_values').toArray()) as unknown as ValueRecord[]),
        shares: maps.some((m) => m.matrix.filter?.groupIds?.length)
          ? alive((await db.synced('item_shares').toArray()) as unknown as ShareRecord[])
          : [],
      }
    },
    { maps: [], defs: [], values: [], shares: [] } as Lookups,
  )

  const maps = computed(() => lookups.value.value.maps)
  const columns = computed<ColumnDef[]>(() =>
    maps.value.map((m) => ({ id: `${QUADRANT_COLUMN}${m.id}`, label: `Quadrant · ${m.name}`, width: '12rem' })),
  )

  /** Everything the per-item lookups need, rebuilt when the data changes; answers memoised per item row. */
  const index = computed(() => {
    const { defs, values, shares } = lookups.value.value
    const defsByKey: Record<string, FieldDefinition> = {}
    const keyOf = new Map<string, string>()
    for (const d of defs) {
      defsByKey[d.key] = {
        id: d.id,
        key: d.key,
        label: d.label,
        type: d.type,
        config: d.config,
        isArchived: d.isArchived,
      }
      keyOf.set(d.id, d.key)
    }
    const valuesByItem = new Map<string, Record<string, unknown>>()
    for (const v of values) {
      const key = keyOf.get(v.fieldId)
      if (!key) continue
      let mine = valuesByItem.get(v.itemId)
      if (!mine) valuesByItem.set(v.itemId, (mine = {}))
      mine[key] = v.value
    }
    const groupsByItem = new Map<string, Set<string>>()
    for (const s of shares) {
      let set = groupsByItem.get(s.itemId)
      if (!set) groupsByItem.set(s.itemId, (set = new Set()))
      set.add(s.groupId)
    }
    return {
      defsByKey,
      valuesByItem,
      sharedWith: (itemId: string, groupIds: readonly string[]) =>
        groupIds.some((g) => groupsByItem.get(itemId)?.has(g)),
      byMap: new Map(maps.value.map((m) => [m.id, m])),
      memo: new WeakMap<ItemRow, Map<string, ItemQuadrant | null>>(),
    }
  })

  function quadrantFor(item: ItemRow, mapId: string): ItemQuadrant | null {
    const ix = index.value
    let mine = ix.memo.get(item)
    if (!mine) ix.memo.set(item, (mine = new Map()))
    if (mine.has(mapId)) return mine.get(mapId)!
    const map = ix.byMap.get(mapId)
    const result = map
      ? (quadrantsOf(map.matrix, [toMapRow<MapItemRow>(item)], ix.defsByKey, ix.valuesByItem, ix.sharedWith).get(
          item.id,
        ) ?? null)
      : null
    mine.set(mapId, result)
    return result
  }

  return {
    maps,
    columns,
    quadrantFor,
    /** The quadrant columns' value: the quadrant's label. */
    columnValue: (item: ItemRow, id: string): unknown =>
      id.startsWith(QUADRANT_COLUMN) ? (quadrantFor(item, id.slice(QUADRANT_COLUMN.length))?.label ?? null) : undefined,
    quadrantOf: (item: ItemRow, mapId: string): Quadrant | null => quadrantFor(item, mapId)?.quadrant ?? null,
  }
}
