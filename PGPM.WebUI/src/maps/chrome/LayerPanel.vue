<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { computed, nextTick, ref } from 'vue'
import type { LayerRow } from '../types'

/**
 * The layer panel (doc 01 §12.6): top layer first; rename (double-click), up/down, show/hide, lock, delete – a layer
 * with elements asks whether they move to the layer below or go with it. Visibility and lock are shared; with read
 * access the eye hides a layer for oneself only. A click makes a layer the one new elements go to.
 */
const props = defineProps<{
  /** Bottom first, as stored. */
  layers: readonly LayerRow[]
  activeId: string | null
  canEdit: boolean
  hiddenLocally: ReadonlySet<string>
  counts: ReadonlyMap<string, number>
}>()
const emit = defineEmits<{
  add: []
  activate: [id: string]
  rename: [id: string, name: string]
  visible: [id: string, visible: boolean]
  hideLocally: [id: string, hidden: boolean]
  locked: [id: string, locked: boolean]
  move: [id: string, direction: 'up' | 'down']
  delete: [id: string, mode: 'move' | 'delete']
  close: []
}>()

const topFirst = computed(() => [...props.layers].reverse())
const renaming = ref<string | null>(null)
const name = ref('')
const confirming = ref<string | null>(null)
const panel = ref<HTMLElement | null>(null)

async function startRename(layer: LayerRow) {
  if (!props.canEdit) return
  renaming.value = layer.id
  name.value = layer.name
  await nextTick()
  panel.value?.querySelector<HTMLInputElement>('.name-input')?.select()
}

function commitRename() {
  const id = renaming.value
  renaming.value = null
  const next = name.value.trim()
  const layer = props.layers.find((l) => l.id === id)
  if (id && layer && next && next !== layer.name) emit('rename', id, next)
}

function shown(layer: LayerRow) {
  return layer.is_visible && !props.hiddenLocally.has(layer.id)
}

function toggleVisible(layer: LayerRow) {
  if (props.canEdit) emit('visible', layer.id, !layer.is_visible)
  else emit('hideLocally', layer.id, !props.hiddenLocally.has(layer.id))
}

function remove(layer: LayerRow) {
  if ((props.counts.get(layer.id) ?? 0) > 0) confirming.value = layer.id
  else emit('delete', layer.id, 'delete')
}

function decide(mode: 'move' | 'delete') {
  if (confirming.value) emit('delete', confirming.value, mode)
  confirming.value = null
}
</script>

<template>
  <section ref="panel" class="layer-panel panel" aria-label="Layers">
    <header>
      <h3>Layers</h3>
      <Button v-if="canEdit" icon="pi pi-plus" size="small" text aria-label="Add layer" @click="emit('add')" />
      <Button icon="pi pi-times" size="small" text severity="secondary" aria-label="Close" @click="emit('close')" />
    </header>
    <ul>
      <li
        v-for="(layer, i) in topFirst"
        :key="layer.id"
        :class="{ active: layer.id === activeId, hidden: !shown(layer) }"
        :data-layer="layer.id"
      >
        <template v-if="confirming === layer.id">
          <div class="confirm">
            <span>{{ counts.get(layer.id) }} element(s) on “{{ layer.name }}”:</span>
            <Button label="Move them down" size="small" @click="decide('move')" />
            <Button label="Delete them" size="small" severity="danger" text @click="decide('delete')" />
            <Button label="Cancel" size="small" severity="secondary" text @click="confirming = null" />
          </div>
        </template>
        <template v-else>
          <Button
            :icon="shown(layer) ? 'pi pi-eye' : 'pi pi-eye-slash'"
            size="small"
            text
            severity="secondary"
            :aria-label="shown(layer) ? `Hide ${layer.name}` : `Show ${layer.name}`"
            @click="toggleVisible(layer)"
          />
          <InputText
            v-if="renaming === layer.id"
            v-model="name"
            size="small"
            class="name-input"
            aria-label="Layer name"
            @keydown.enter="commitRename"
            @keydown.esc="renaming = null"
            @blur="commitRename"
          />
          <button
            v-else
            type="button"
            class="name"
            :title="canEdit ? 'Click: new elements go here · double-click: rename' : undefined"
            @click="emit('activate', layer.id)"
            @dblclick="startRename(layer)"
          >
            {{ layer.name }} <span class="muted">{{ counts.get(layer.id) ?? 0 }}</span>
          </button>
          <template v-if="canEdit">
            <Button
              :icon="layer.is_locked ? 'pi pi-lock' : 'pi pi-lock-open'"
              size="small"
              text
              :severity="layer.is_locked ? undefined : 'secondary'"
              :aria-label="layer.is_locked ? `Unlock ${layer.name}` : `Lock ${layer.name}`"
              @click="emit('locked', layer.id, !layer.is_locked)"
            />
            <Button
              icon="pi pi-angle-up"
              size="small"
              text
              severity="secondary"
              :disabled="i === 0"
              :aria-label="`Move ${layer.name} up`"
              @click="emit('move', layer.id, 'up')"
            />
            <Button
              icon="pi pi-angle-down"
              size="small"
              text
              severity="secondary"
              :disabled="i === topFirst.length - 1"
              :aria-label="`Move ${layer.name} down`"
              @click="emit('move', layer.id, 'down')"
            />
            <Button
              icon="pi pi-trash"
              size="small"
              text
              severity="secondary"
              :disabled="layers.length < 2"
              :aria-label="`Delete ${layer.name}`"
              @click="remove(layer)"
            />
          </template>
          <i v-else-if="layer.is_locked" role="img" class="pi pi-lock muted" aria-label="Locked" />
        </template>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.layer-panel {
  width: 18rem;
  max-height: 60vh;
  overflow-y: auto;
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 12%);
  padding: var(--pgpm-space-1) var(--pgpm-space-2) var(--pgpm-space-2);
}

header {
  display: flex;
  align-items: center;
  gap: 2px;
}

h3 {
  flex: 1;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

ul {
  list-style: none;
  margin: 0;
  padding: 0;
}

li {
  display: flex;
  align-items: center;
  gap: 0;
  border-radius: var(--p-border-radius-sm);
}

li.active {
  background: var(--p-highlight-background);
}

li.hidden .name {
  opacity: 0.55;
}

.name {
  flex: 1;
  min-width: 0;
  text-align: left;
  border: 0;
  background: none;
  color: var(--pgpm-text);
  font: inherit;
  padding: 4px;
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.name-input {
  flex: 1;
  min-width: 0;
}

.confirm {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-1);
  padding: var(--pgpm-space-1);
  font-size: 0.857rem;
}
</style>
