<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Textarea from 'primevue/textarea'
import { ref, watch } from 'vue'
import type { MapRow } from '../types'
import ColorField from './ColorField.vue'

/**
 * Nothing selected → the map's properties (doc 01 §12.5): name, description, background, grid, snap, the cards'
 * badges; sharing for managers is the host's (`#map-sharing`). Saved at once; read access shows them only.
 */
const props = defineProps<{
  map: MapRow
  canEdit: boolean
  badgeFields: { value: string; label: string }[]
}>()
const emit = defineEmits<{ patch: [values: Record<string, unknown>] }>()

const name = ref(props.map.name)
const description = ref(props.map.description ?? '')
watch(
  () => props.map,
  (m) => {
    name.value = m.name
    description.value = m.description ?? ''
  },
)

function commitName() {
  const next = name.value.trim()
  if (next && next !== props.map.name) emit('patch', { name: next })
  else name.value = props.map.name
}

function commitDescription() {
  const next = description.value.trim() || null
  if (next !== (props.map.description ?? null)) emit('patch', { description: next })
}

const s = () => props.map.settings ?? {}
</script>

<template>
  <section class="form">
    <h3>Map</h3>
    <div class="field">
      <label for="map-name">Name</label>
      <InputText
        id="map-name"
        v-model="name"
        size="small"
        :disabled="!canEdit"
        @keydown.enter="commitName"
        @blur="commitName"
      />
    </div>
    <div class="field">
      <label for="map-description">Description</label>
      <Textarea
        id="map-description"
        v-model="description"
        rows="3"
        auto-resize
        :disabled="!canEdit"
        @blur="commitDescription"
      />
    </div>
    <div class="field">
      <label>Background</label>
      <ColorField
        :model-value="s().background ?? null"
        label="Background"
        :disabled="!canEdit"
        @update:model-value="(v) => emit('patch', { 'settings.background': v ?? undefined })"
      />
    </div>
    <div class="checks">
      <label class="check">
        <Checkbox
          :model-value="s().grid ?? false"
          binary
          input-id="map-grid"
          :disabled="!canEdit"
          @update:model-value="(v: boolean) => emit('patch', { 'settings.grid': v })"
        />
        <span>Grid</span>
      </label>
      <label class="check">
        <Checkbox
          :model-value="s().snap ?? false"
          binary
          input-id="map-snap"
          :disabled="!canEdit"
          @update:model-value="(v: boolean) => emit('patch', { 'settings.snap': v })"
        />
        <span>Snap to grid</span>
      </label>
      <label class="check" for="map-grid-size">
        <span>Grid size</span>
        <InputNumber
          :model-value="s().gridSize ?? 20"
          input-id="map-grid-size"
          size="small"
          :min="4"
          :max="200"
          :disabled="!canEdit"
          input-class="number"
          @update:model-value="(v: number | null) => v && emit('patch', { 'settings.gridSize': v })"
        />
      </label>
    </div>
    <div v-if="badgeFields.length" class="field">
      <label id="map-badges-label" for="map-badges">Badges on cards</label>
      <MultiSelect
        :model-value="s().cardBadges ?? []"
        :options="badgeFields"
        option-label="label"
        option-value="value"
        input-id="map-badges"
        aria-labelledby="map-badges-label"
        size="small"
        display="chip"
        filter
        placeholder="Scores, ratings, numbers"
        :disabled="!canEdit"
        @update:model-value="(v: string[]) => emit('patch', { 'settings.cardBadges': v })"
      />
    </div>
  </section>
  <slot name="sharing" />
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.field > label {
  font-size: 0.857rem;
  color: var(--pgpm-muted);
}

.checks {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pgpm-space-4);
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.form :deep(.number) {
  width: 4.5rem;
}
</style>
