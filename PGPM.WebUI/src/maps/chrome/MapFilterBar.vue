<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed } from 'vue'
import { activeConditions } from '../filter'
import type { Option } from '../sceneTypes'
import { FILTER_TYPES, type FilterType, type MapFilter } from '../types'

/**
 * The visibility filter above the map (doc 01 §12.7), a personal view setting: types, statuses, persons, colours,
 * project templates, quadrants (matrix maps), a score range; *Show links*. Managers can save it as the map's default
 * view.
 */
const props = defineProps<{
  filter: MapFilter
  statuses: Option[]
  persons: Option[]
  colors: Option[]
  templates?: Option[]
  scoreFields: Option[]
  quadrants: Option[] | null
  showLinks: boolean
  canSaveDefault: boolean
}>()
const emit = defineEmits<{
  update: [filter: MapFilter]
  showLinks: [on: boolean]
  saveDefault: []
}>()

const TYPE_LABELS: Record<FilterType, string> = {
  note: 'Notes',
  idea: 'Ideas',
  lead: 'Leads',
  project: 'Projects',
  milestone: 'Milestones',
  drawing: 'Drawings',
}
const typeOptions = FILTER_TYPES.map((t) => ({ value: t, label: TYPE_LABELS[t] }))

function set<K extends keyof MapFilter>(key: K, value: MapFilter[K]) {
  emit('update', { ...props.filter, [key]: value })
}

/** A multi-select: nothing chosen means no restriction (null). */
function list<K extends 'statusIds' | 'personIds' | 'colors' | 'templateIds' | 'quadrants'>(key: K) {
  return computed({
    get: () => (props.filter[key] ?? []) as string[],
    set: (v: string[]) => set(key, (v.length ? v : null) as MapFilter[K]),
  })
}
const statusIds = list('statusIds')
const personIds = list('personIds')
const colorKeys = list('colors')
const templateIds = list('templateIds')
const quadrantKeys = list('quadrants')
const types = computed({
  get: () => props.filter.types ?? [...FILTER_TYPES],
  set: (v: FilterType[]) => set('types', v.length === FILTER_TYPES.length ? null : v),
})
const scoreField = computed({
  get: () => props.filter.score?.field ?? null,
  set: (field: string | null) => set('score', field ? { ...props.filter.score, field } : null),
})
function scoreBound(bound: 'min' | 'max', value: number | null) {
  if (props.filter.score) set('score', { ...props.filter.score, [bound]: value })
}
const active = computed(() => activeConditions(props.filter))
</script>

<template>
  <div class="map-filter-bar panel" role="search" aria-label="Map filter">
    <MultiSelect
      v-model="types"
      :options="typeOptions"
      option-label="label"
      option-value="value"
      size="small"
      placeholder="Types"
      :max-selected-labels="2"
      selected-items-label="{0} types"
      aria-label="Element types"
      class="types"
    />
    <MultiSelect
      v-if="statuses.length"
      v-model="statusIds"
      :options="statuses"
      option-label="label"
      option-value="value"
      size="small"
      filter
      placeholder="Status"
      :max-selected-labels="1"
      selected-items-label="{0} statuses"
      aria-label="Statuses"
    />
    <MultiSelect
      v-if="persons.length"
      v-model="personIds"
      :options="persons"
      option-label="label"
      option-value="value"
      size="small"
      filter
      placeholder="Persons"
      :max-selected-labels="1"
      selected-items-label="{0} persons"
      aria-label="Assigned persons"
    />
    <MultiSelect
      v-if="colors.length > 1"
      v-model="colorKeys"
      :options="colors"
      option-label="label"
      option-value="value"
      size="small"
      placeholder="Colour"
      :max-selected-labels="1"
      selected-items-label="{0} colours"
      aria-label="Colours"
    />
    <MultiSelect
      v-if="templates?.length"
      v-model="templateIds"
      :options="templates"
      option-label="label"
      option-value="value"
      size="small"
      placeholder="Template"
      :max-selected-labels="1"
      selected-items-label="{0} templates"
      aria-label="Project templates"
    />
    <MultiSelect
      v-if="quadrants"
      v-model="quadrantKeys"
      :options="quadrants"
      option-label="label"
      option-value="value"
      size="small"
      placeholder="Quadrant"
      :max-selected-labels="1"
      selected-items-label="{0} quadrants"
      aria-label="Quadrants"
    />
    <template v-if="scoreFields.length">
      <Select
        v-model="scoreField"
        :options="scoreFields"
        option-label="label"
        option-value="value"
        size="small"
        show-clear
        placeholder="Score"
        aria-label="Score range field"
      />
      <template v-if="filter.score?.field">
        <InputNumber
          :model-value="filter.score.min ?? null"
          size="small"
          placeholder="min"
          input-class="bound"
          aria-label="Score at least"
          @update:model-value="(v: number | null) => scoreBound('min', v)"
        />
        <InputNumber
          :model-value="filter.score.max ?? null"
          size="small"
          placeholder="max"
          input-class="bound"
          aria-label="Score at most"
          @update:model-value="(v: number | null) => scoreBound('max', v)"
        />
      </template>
    </template>
    <label class="check">
      <Checkbox
        :model-value="showLinks"
        binary
        input-id="map-show-links"
        @update:model-value="(v: boolean) => emit('showLinks', v)"
      />
      <span>Show links</span>
    </label>
    <Button
      v-if="active"
      :label="`Reset (${active})`"
      size="small"
      text
      severity="secondary"
      icon="pi pi-filter-slash"
      @click="emit('update', {})"
    />
    <Button
      v-if="canSaveDefault"
      v-tooltip.bottom="'Save this filter as the map\'s default view'"
      label="Save as default"
      size="small"
      text
      severity="secondary"
      @click="emit('saveDefault')"
    />
  </div>
</template>

<style scoped>
.map-filter-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 10%);
}

.map-filter-bar :deep(.bound) {
  width: 4.5rem;
}

.types {
  min-width: 9rem;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.929rem;
  white-space: nowrap;
}
</style>
