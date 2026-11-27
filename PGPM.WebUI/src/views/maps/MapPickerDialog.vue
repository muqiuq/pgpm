<script setup lang="ts">
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { localDb } from '@/local/db'
import type { MapRecord } from '@/local/maps'
import { useLive } from '@/local/useLive'
import { KIND_ICONS } from './mapList'

/**
 * *Add to map…* (docs/01 §4.3, §12.4): the canvas maps the user may edit (not templates), searchable by name; a click
 * picks one. `placements` marks the maps the item is on already (how often); `hint` says where the cards go.
 */
const props = withDefaults(
  defineProps<{ header?: string; hint: string; placements?: ReadonlyMap<string, number> | null }>(),
  { header: 'Add to map', placements: null },
)
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ pick: [map: MapRecord] }>()

const maps = useLive(
  async () => ((await localDb().synced('maps').toArray()) as unknown as MapRecord[]).filter((m) => !m.deleted),
  [] as MapRecord[],
)
const query = ref('')
watch(visible, (open) => {
  if (open) query.value = ''
})
const targets = computed(() => {
  const q = query.value.trim().toLowerCase()
  return maps.value.value
    .filter((m) => m.kind === 'canvas' && !m.isTemplate && (m.access === 'edit' || m.access === 'manage'))
    .filter((m) => !q || m.name.toLowerCase().includes(q))
    .sort((a, b) => a.name.localeCompare(b.name))
})
</script>

<template>
  <Dialog v-model:visible="visible" :header="props.header" modal :style="{ width: 'min(30rem, 100vw)' }">
    <InputText
      v-model="query"
      placeholder="Search maps…"
      size="small"
      class="search"
      aria-label="Search maps"
      autofocus
    />
    <ul class="choices" aria-label="Canvas maps">
      <li v-for="m in targets" :key="m.id">
        <button type="button" :data-map="m.id" @click="emit('pick', m)">
          <i :class="KIND_ICONS[m.kind]" aria-hidden="true" />
          <span class="label">{{ m.name }}</span>
          <span v-if="placements?.get(m.id)" class="muted small">on it · {{ placements.get(m.id) }}×</span>
        </button>
      </li>
      <li v-if="maps.loaded.value && !targets.length" class="muted">No canvas map you can edit.</li>
    </ul>
    <small class="muted">{{ hint }}</small>
  </Dialog>
</template>

<style scoped>
.search {
  width: 100%;
}

.choices {
  list-style: none;
  margin: var(--pgpm-space-2) 0;
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

.small {
  font-size: 0.857rem;
}
</style>
