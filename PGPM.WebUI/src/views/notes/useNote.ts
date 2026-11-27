import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb } from '@/local/db'
import { allowsAccess, type ItemRow, type ItemShareRow, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'

/** A note from the local data, live: the row, its shares, its conflicts and what the user may do with it. */
export function useNote(id: MaybeRefOrGetter<string>) {
  const note = useLive(
    async () => {
      const row = (await localDb().synced('items').get(toValue(id))) as Stored<ItemRow> | undefined
      return row?.type === 'note' ? row : undefined
    },
    undefined as Stored<ItemRow> | undefined,
    () => toValue(id),
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
    () => toValue(id),
  )
  const conflicts = useLive(
    () =>
      localDb()
        .conflicts.where('[table+rowId]')
        .equals(['items', toValue(id)])
        .toArray(),
    [] as ConflictEntry[],
    () => toValue(id),
  )
  const canEdit = computed(
    () => !!note.value.value && !note.value.value.deleted && allowsAccess(note.value.value.access, 'edit'),
  )
  const canManage = computed(() => !!note.value.value && allowsAccess(note.value.value.access, 'manage'))
  return { note: note.value, loaded: note.loaded, shares: shares.value, conflicts: conflicts.value, canEdit, canManage }
}
