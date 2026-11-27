<script setup lang="ts">
import Dialog from 'primevue/dialog'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, nextTick, ref, watch } from 'vue'
import { itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { itemKey, type ItemRow, type ItemType } from '@/local/rows'
import { itemRank } from '@/local/search'
import { useLive } from '@/local/useLive'

/**
 * Picks an item from the local data (docs/01 §4.7 *Links*, §4.10 *Link to an item…*): a search over key, title and
 * description (every word must occur), the latest changed first without a query; ↑/↓ and Enter pick, offline too.
 * `types` narrows the types, `exclude` leaves items out.
 */
const props = withDefaults(
  defineProps<{ header?: string; types?: readonly ItemType[] | null; exclude?: readonly string[] }>(),
  { header: 'Pick an item', types: null, exclude: () => [] },
)
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ pick: [item: ItemRow] }>()

const query = ref('')
const active = ref(0)
const input = ref<{ $el?: HTMLElement } | null>(null)

const items = useLive(
  async () => ((await localDb().synced('items').toArray()) as unknown as ItemRow[]).filter((i) => !i.deleted),
  [] as ItemRow[],
)

const results = computed(() => {
  const candidates = items.value.value.filter(
    (i) => (!props.types || props.types.includes(i.type)) && !props.exclude.includes(i.id),
  )
  const q = query.value.trim()
  if (!q) return [...candidates].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, 20)
  return candidates
    .map((item) => ({ item, rank: itemRank(item, q) }))
    .filter((r): r is { item: ItemRow; rank: number } => r.rank !== null)
    .sort((a, b) => a.rank - b.rank || b.item.updatedAt.localeCompare(a.item.updatedAt))
    .slice(0, 30)
    .map((r) => r.item)
})

watch(results, () => (active.value = 0))
watch(visible, async (open) => {
  if (!open) return
  query.value = ''
  active.value = 0
  await nextTick()
  input.value?.$el?.focus()
})

function pick(item: ItemRow | undefined) {
  if (!item) return
  emit('pick', item)
  visible.value = false
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    active.value = Math.min(active.value + 1, results.value.length - 1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    active.value = Math.max(active.value - 1, 0)
  } else if (event.key === 'Enter') {
    event.preventDefault()
    pick(results.value[active.value])
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="header" modal :style="{ width: 'min(36rem, 100vw)' }">
    <IconField>
      <InputIcon class="pi pi-search" />
      <InputText
        ref="input"
        v-model="query"
        size="small"
        placeholder="Key, title or text…"
        aria-label="Search items"
        role="combobox"
        aria-controls="item-picker-list"
        :aria-expanded="results.length > 0"
        :aria-activedescendant="results[active] ? `item-picker-${results[active]!.id}` : undefined"
        fluid
        @keydown="onKey"
      />
    </IconField>
    <ul id="item-picker-list" class="results" role="listbox" aria-label="Items">
      <li
        v-for="(item, i) in results"
        :id="`item-picker-${item.id}`"
        :key="item.id"
        role="option"
        :aria-selected="i === active"
        :class="{ active: i === active }"
        @mouseenter="active = i"
        @click="pick(item)"
      >
        <i :class="itemTypes[item.type].icon" aria-hidden="true" />
        <span class="mono muted key">{{ itemKey(item) }}</span>
        <span class="ellipsis">{{ item.title }}</span>
      </li>
      <li v-if="!results.length" class="muted none" role="presentation">Nothing found.</li>
    </ul>
  </Dialog>
</template>

<style scoped>
.results {
  list-style: none;
  margin: var(--pgpm-space-2) 0 0;
  padding: 0;
  max-height: 22rem;
  overflow-y: auto;
}

.results li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-radius: var(--p-border-radius-sm);
  cursor: pointer;
  min-height: 32px;
}

.results li.active {
  background: var(--p-highlight-background);
}

.results li.none {
  cursor: default;
}

.key {
  flex: none;
  min-width: 4.5rem;
}
</style>
