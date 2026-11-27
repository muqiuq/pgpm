import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb } from '@/local/db'
import { convertedProject } from '@/local/items'
import { allowsAccess, type ItemRow, type ItemShareRow, type ItemType, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * An item of a type from the local data, live: the row, its shares, its conflicts, the project it was converted into
 * (ideas, leads) and what the user may do with it. A row of another type is not this page's (undefined).
 */
export function useItem(type: MaybeRefOrGetter<ItemType>, id: MaybeRefOrGetter<string>) {
  const key = () => `${toValue(type)}:${toValue(id)}`
  const item = useLive(
    async () => {
      const row = (await localDb().synced('items').get(toValue(id))) as Stored<ItemRow> | undefined
      return row?.type === toValue(type) ? row : undefined
    },
    undefined as Stored<ItemRow> | undefined,
    key,
  )
  const shares = useLive(
    async () =>
      (
        (await localDb()
          .synced('item_shares')
          .where('itemId')
          .equals(toValue(id))
          .toArray()) as unknown as ItemShareRow[]
      ).filter((s) => !s.deleted),
    [] as ItemShareRow[],
    key,
  )
  const conflicts = useLive(
    () =>
      localDb()
        .conflicts.where('[table+rowId]')
        .equals(['items', toValue(id)])
        .toArray(),
    [] as ConflictEntry[],
    key,
  )
  const converted = useLive(
    async () => {
      const row = (await localDb().synced('items').get(toValue(id))) as Stored<ItemRow> | undefined
      return row && (row.type === 'idea' || row.type === 'lead') ? await convertedProject(localDb(), row) : undefined
    },
    undefined as Stored<ItemRow> | undefined,
    key,
  )
  const origin = useLive(
    async () => {
      const row = (await localDb().synced('items').get(toValue(id))) as Stored<ItemRow> | undefined
      if (!row?.originItemId) return undefined
      const source = (await localDb().synced('items').get(row.originItemId)) as Stored<ItemRow> | undefined
      return source && !source.deleted ? source : null
    },
    undefined as Stored<ItemRow> | null | undefined,
    key,
  )
  const canEdit = computed(
    () => !!item.value.value && !item.value.value.deleted && allowsAccess(item.value.value.access, 'edit'),
  )
  const canManage = computed(() => !!item.value.value && allowsAccess(item.value.value.access, 'manage'))
  return {
    item: item.value,
    loaded: item.loaded,
    shares: shares.value,
    conflicts: conflicts.value,
    converted: converted.value,
    origin: origin.value,
    canEdit,
    canManage,
  }
}
