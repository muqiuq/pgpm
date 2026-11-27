<script setup lang="ts">
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed } from 'vue'
import { DEFAULT_QUADRANT_LABELS, type Quadrant } from '@/maps/matrixLayout'
import type { QuadrantMap } from './mapList'

/**
 * The item lists' quadrant filter (doc 01 §12.8): a matrix map, then the quadrants an item must be in there (items
 * without both values are in none).
 */
const props = defineProps<{ maps: readonly QuadrantMap[] }>()
const model = defineModel<{ mapId: string; quadrants: Quadrant[] } | undefined>()

const mapId = computed(() => model.value?.mapId ?? props.maps[0]?.id ?? null)
const options = computed(() => {
  const labels = props.maps.find((m) => m.id === mapId.value)?.matrix.quadrants ?? {}
  return (['tr', 'tl', 'br', 'bl'] as Quadrant[]).map((q) => ({
    value: q,
    label: labels[q] || DEFAULT_QUADRANT_LABELS[q],
  }))
})

function setMap(id: string | null) {
  model.value = id ? { mapId: id, quadrants: model.value?.quadrants ?? [] } : undefined
}
function setQuadrants(quadrants: Quadrant[]) {
  model.value = mapId.value && quadrants.length ? { mapId: mapId.value, quadrants } : undefined
}
</script>

<template>
  <div class="quadrant-filter field">
    <label id="filter-quadrant-label" for="filter-quadrant-map">Quadrant</label>
    <Select
      v-if="maps.length > 1"
      :model-value="mapId"
      input-id="filter-quadrant-map"
      aria-labelledby="filter-quadrant-label"
      :options="[...maps]"
      option-label="name"
      option-value="id"
      size="small"
      @update:model-value="setMap"
    />
    <MultiSelect
      :model-value="model?.quadrants ?? []"
      :options="options"
      option-label="label"
      option-value="value"
      :placeholder="maps.length === 1 ? `Any quadrant of ${maps[0]!.name}` : 'Any quadrant'"
      size="small"
      :input-id="maps.length > 1 ? undefined : 'filter-quadrant-map'"
      aria-labelledby="filter-quadrant-label"
      @update:model-value="setQuadrants"
    />
  </div>
</template>

<style scoped>
.quadrant-filter {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  margin-top: var(--pgpm-space-3);
  min-width: 16rem;
}
</style>
