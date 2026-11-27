<script setup lang="ts">
import { computed } from 'vue'
import ItemSections from '@/components/items/ItemSections.vue'
import { localDb } from '@/local/db'
import { useMarkdownFiles } from '@/local/files'
import type { ItemRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * An idea's or project's sections in the map's side panel (ItemQuickEdit's `#sections` slot, docs/03 §13.7): phase 4's
 * `ItemSections` as on the item's Overview – values saved on the device at once, scores live, read-only where the
 * section or the item is.
 */
const props = defineProps<{ itemId: string; canEdit: boolean }>()
const files = useMarkdownFiles(() => props.itemId)

const row = useLive(
  async () => (await localDb().synced('items').get(props.itemId)) as unknown as Stored<ItemRow> | undefined,
  undefined as Stored<ItemRow> | undefined,
  () => props.itemId,
)
const item = computed(() => {
  const it = row.value.value
  return it && !it.deleted && (it.type === 'idea' || it.type === 'project') ? it : null
})
</script>

<template>
  <section v-if="item" class="form map-item-sections" aria-label="Sections">
    <h3>Sections</h3>
    <ItemSections :item="item" :can-edit="canEdit" :files="files" />
  </section>
</template>

<style scoped>
.map-item-sections {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

h3 {
  margin: 0;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}
</style>
