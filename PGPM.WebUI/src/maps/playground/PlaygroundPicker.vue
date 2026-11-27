<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { itemKey, TYPE_ICONS, TYPE_LABELS } from '../cards'
import type { MemorySource } from '../memorySource'
import type { ItemRow } from '../types'
import type { Pending } from './pending'
import { sampleId } from './sampleData'

/**
 * The playground's stand-in for the host's dialogs: an item tool's *Create new* (a title) or *Add existing* (items
 * already on the map are marked, and can be added again); a milestone or a project for the milestone and timeline
 * tools.
 */
const props = defineProps<{ pending: Pending | null; source: MemorySource }>()
const emit = defineEmits<{ done: [] }>()

const title = ref('')
const search = ref('')
watch(
  () => props.pending,
  () => {
    title.value = ''
    search.value = ''
  },
)

const heading = computed(() => {
  const p = props.pending
  if (!p) return ''
  if (p.kind === 'item') return `Add ${TYPE_LABELS[p.request.extra.type].toLowerCase()}`
  return p.kind === 'milestone' ? 'Add a milestone or gate' : 'Add a project timeline'
})

const choices = computed(() => {
  const p = props.pending
  const q = search.value.trim().toLowerCase()
  if (!p) return []
  if (p.kind === 'milestone') {
    const projects = new Map(props.source.items.value.map((i) => [i.id, i]))
    return props.source.milestones.value
      .map((m) => ({
        id: m.id,
        icon: m.kind === 'gate' ? 'pi pi-stop' : 'pi pi-circle',
        label: `${m.title} · ${projects.get(m.item_id)?.title ?? ''}`,
        onMap: false,
      }))
      .filter((c) => c.label.toLowerCase().includes(q))
  }
  const type = p.kind === 'project' ? 'project' : p.request.extra.type
  return props.source.items.value
    .filter((i) => i.type === type && `${itemKey(i)} ${i.title}`.toLowerCase().includes(q))
    .map((i) => ({
      id: i.id,
      icon: TYPE_ICONS[i.type],
      label: `${itemKey(i)} ${i.title}`,
      onMap: p.request.onMap.has(i.id),
    }))
})

function choose(id: string) {
  props.pending?.request.place(id)
  emit('done')
}

function create() {
  const p = props.pending
  if (p?.kind !== 'item' || !title.value.trim()) return
  const type = p.request.extra.type
  const numbers = props.source.items.value.filter((i) => i.type === type).map((i) => i.number ?? 0)
  const status = props.source.statuses.value.find((s) => s.item_type === type)
  const item: ItemRow = {
    id: sampleId(20),
    type,
    number: Math.max(0, ...numbers) + 1,
    title: title.value.trim(),
    description: null,
    color: null,
    status_id: status?.id ?? null,
    status_changed_at: status ? new Date().toISOString() : null,
  }
  props.source.put('items', item)
  choose(item.id)
}

function cancel() {
  props.pending?.request.cancel()
  emit('done')
}
</script>

<template>
  <Dialog
    :visible="pending !== null"
    modal
    :header="heading"
    :style="{ width: 'min(30rem, 95vw)' }"
    @update:visible="(v: boolean) => !v && cancel()"
  >
    <form v-if="pending?.kind === 'item'" class="create" @submit.prevent="create">
      <InputText v-model="title" placeholder="Title of the new item" size="small" autofocus aria-label="Title" />
      <Button type="submit" label="Create new" size="small" :disabled="!title.trim()" />
    </form>
    <InputText v-model="search" placeholder="Search…" size="small" class="search" aria-label="Search" />
    <ul class="choices">
      <li v-for="c in choices" :key="c.id">
        <button type="button" @click="choose(c.id)">
          <i :class="c.icon" aria-hidden="true" />
          <span class="label">{{ c.label }}</span>
          <span v-if="c.onMap" class="muted">on the map</span>
        </button>
      </li>
      <li v-if="!choices.length" class="muted">Nothing found.</li>
    </ul>
  </Dialog>
</template>

<style scoped>
.create {
  display: flex;
  gap: var(--pgpm-space-2);
  margin-bottom: var(--pgpm-space-3);
}

.create :deep(input) {
  flex: 1;
}

.search {
  width: 100%;
}

.choices {
  list-style: none;
  margin: var(--pgpm-space-2) 0 0;
  padding: 0;
  max-height: 50vh;
  overflow-y: auto;
}

.choices button {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  width: 100%;
  padding: 6px 4px;
  border: 0;
  background: none;
  color: var(--pgpm-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  border-radius: var(--p-border-radius-sm);
}

.choices button:hover {
  background: var(--p-content-hover-background);
}

.label {
  flex: 1;
}
</style>
