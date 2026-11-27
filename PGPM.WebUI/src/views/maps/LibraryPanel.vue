<script setup lang="ts">
import Button from 'primevue/button'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import { computed, ref } from 'vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import { itemTypeList, itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { useStatuses } from '@/local/lookups'
import { itemKey, type ItemRow, type ItemType } from '@/local/rows'
import { itemRank } from '@/local/search'
import { useLive } from '@/local/useLive'
import { ITEM_DRAG_TYPE } from './mapList'

/**
 * The map's library panel (doc 01 §12.4): every item on the device, searchable (key, title, description – every word
 * must occur) and filterable by type and status; a row is dragged onto the canvas (`application/x-pgpm-item`, the host
 * drops it with `addItemCard` where it lands) or added in the middle of the view with its *+*. Items already on the map
 * show how often.
 */
const LIMIT = 100

const props = defineProps<{ counts: ReadonlyMap<string, number>; canAdd: boolean }>()
const emit = defineEmits<{ add: [itemId: string]; close: [] }>()
const statuses = useStatuses()

const query = ref('')
const types = ref<ItemType[]>([])
const statusIds = ref<string[]>([])

const items = useLive(
  async () => ((await localDb().synced('items').toArray()) as unknown as ItemRow[]).filter((i) => !i.deleted),
  [] as ItemRow[],
)
const typeOptions = itemTypeList.map((t) => ({ value: t, label: itemTypes[t].plural }))
const statusOptions = computed(() =>
  statuses.all.value
    .filter((s) => !s.isArchived && (!types.value.length || types.value.includes(s.itemType)))
    .map((s) => ({ value: s.id, label: `${s.name} (${itemTypes[s.itemType].label})` })),
)

const matching = computed(() => {
  const q = query.value.trim()
  const list = items.value.value.filter(
    (i) =>
      (!types.value.length || types.value.includes(i.type)) &&
      (!statusIds.value.length || (!!i.statusId && statusIds.value.includes(i.statusId))),
  )
  if (!q) return [...list].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  return list
    .map((item) => ({ item, rank: itemRank(item, q) }))
    .filter((r): r is { item: ItemRow; rank: number } => r.rank !== null)
    .sort((a, b) => a.rank - b.rank || b.item.updatedAt.localeCompare(a.item.updatedAt))
    .map((r) => r.item)
})
const shown = computed(() => matching.value.slice(0, LIMIT))

function onDragStart(event: DragEvent, item: ItemRow) {
  if (!props.canAdd || !event.dataTransfer) return
  event.dataTransfer.setData(ITEM_DRAG_TYPE, item.id)
  event.dataTransfer.setData('text/plain', `${itemKey(item)} ${item.title}`)
  event.dataTransfer.effectAllowed = 'copy'
}
</script>

<template>
  <aside class="library panel" aria-label="Library" data-testid="library">
    <header>
      <h2>Library</h2>
      <Button
        icon="pi pi-times"
        text
        rounded
        size="small"
        severity="secondary"
        aria-label="Close the library"
        @click="emit('close')"
      />
    </header>
    <IconField>
      <InputIcon class="pi pi-search" />
      <InputText v-model="query" placeholder="Search items…" size="small" aria-label="Search items" />
    </IconField>
    <div class="filters">
      <MultiSelect
        v-model="types"
        :options="typeOptions"
        option-label="label"
        option-value="value"
        placeholder="Any type"
        :max-selected-labels="1"
        size="small"
        aria-label="Types"
      />
      <MultiSelect
        v-model="statusIds"
        :options="statusOptions"
        option-label="label"
        option-value="value"
        placeholder="Any status"
        :max-selected-labels="1"
        filter
        size="small"
        aria-label="Statuses"
      />
    </div>
    <p v-if="canAdd" class="muted hint">Drag an item onto the map, or add it in the middle with +.</p>
    <ul class="rows">
      <li
        v-for="item in shown"
        :key="item.id"
        :draggable="canAdd"
        :data-item="item.id"
        class="row"
        @dragstart="(e: DragEvent) => onDragStart(e, item)"
      >
        <i :class="itemTypes[item.type].icon" class="muted" aria-hidden="true" />
        <span class="text">
          <span class="title">{{ item.title }}</span>
          <span class="meta">
            <span class="mono">{{ itemKey(item) }}</span>
            <StatusBadge v-if="item.statusId" :status="statuses.byId.value.get(item.statusId)" />
            <span v-if="counts.get(item.id)" class="on-map">{{ counts.get(item.id) }}× on the map</span>
          </span>
        </span>
        <Button
          v-if="canAdd"
          icon="pi pi-plus"
          text
          rounded
          size="small"
          :aria-label="`Add ${item.title} to the map`"
          @click="emit('add', item.id)"
        />
      </li>
      <li v-if="!shown.length" class="muted">Nothing found.</li>
      <li v-if="matching.length > shown.length" class="muted small">
        {{ matching.length - shown.length }} more – narrow the search.
      </li>
    </ul>
  </aside>
</template>

<style scoped>
.library {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
  width: 18rem;
  min-width: 18rem;
  padding: var(--pgpm-space-2);
  border-right: 1px solid var(--pgpm-border);
  overflow: hidden;
}

header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

h2 {
  margin: 0;
  font-size: 0.857rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.library :deep(.p-iconfield),
.library :deep(.p-iconfield input) {
  width: 100%;
}

.filters {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pgpm-space-1);
}

.filters :deep(.p-multiselect) {
  min-width: 0;
}

.hint {
  margin: 0;
  font-size: 0.786rem;
}

.rows {
  flex: 1;
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
}

.row {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: 4px;
  border-radius: var(--p-border-radius-sm);
}

.row[draggable='true'] {
  cursor: grab;
}

.row:hover {
  background: var(--p-content-hover-background);
}

.text {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  font-size: 0.786rem;
  color: var(--pgpm-muted);
}

.on-map {
  color: var(--p-primary-color);
}

.small {
  font-size: 0.857rem;
}
</style>
