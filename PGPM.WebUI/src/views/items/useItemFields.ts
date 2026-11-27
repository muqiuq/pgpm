import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import type { ConflictEntry } from '@/local/conflicts'
import { useCatalogue } from '@/local/catalogue'
import { localDb } from '@/local/db'
import { fieldValuesOf, itemSectionsOf, scoresOf, valuesByKey } from '@/local/fields'
import type { FieldValueRow, ItemSectionRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * An idea's or project's sections and field values from the local data, live (docs/01 §6.2): the `item_sections` in
 * their order, one value per field, the values by key and the scores computed from them (docs/03 §16 – recomputed
 * whenever a value or the configuration changes), and the sync conflicts of its values (per field).
 */
export function useItemFields(itemId: MaybeRefOrGetter<string>) {
  const { catalogue, loaded: catalogueLoaded } = useCatalogue()
  const key = () => toValue(itemId)
  const sections = useLive(() => itemSectionsOf(localDb(), toValue(itemId)), [] as Stored<ItemSectionRow>[], key)
  const values = useLive(() => fieldValuesOf(localDb(), toValue(itemId)), [] as Stored<FieldValueRow>[], key)
  const conflicts = useLive(
    async () =>
      (await localDb().conflicts.toArray()).filter(
        (c: ConflictEntry) => c.table === 'field_values' && values.value.value.some((v) => v.id === c.rowId),
      ),
    [] as ConflictEntry[],
    () => `${key()}:${values.value.value.map((v) => v.id).join(',')}`,
  )
  const byKey = computed(() => valuesByKey(catalogue.value, values.value.value))
  const scores = computed(() => scoresOf(catalogue.value, byKey.value))
  const valueRows = computed(() => new Map(values.value.value.map((v) => [v.fieldId, v])))
  const conflicted = computed(() => {
    const ids = new Set(conflicts.value.value.map((c) => c.rowId))
    return new Set(values.value.value.filter((v) => ids.has(v.id)).map((v) => v.fieldId))
  })
  return {
    catalogue,
    itemSections: sections.value,
    values: values.value,
    valueRows,
    byKey,
    scores,
    /** Field ids whose value is in a sync conflict. */
    conflicted,
    loaded: computed(() => catalogueLoaded.value && sections.loaded.value && values.loaded.value),
  }
}
