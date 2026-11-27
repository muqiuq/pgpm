<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, reactive, ref, watch } from 'vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { useGroups, useStatuses } from '@/local/lookups'
import type { ItemType, ProjectTemplateRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { DEFAULT_QUADRANT_LABELS, type Quadrant } from '@/maps/matrixLayout'
import type { MatrixSettings } from '@/maps/types'
import { axisFields, type FieldDefinitionRecord } from './mapList'

/**
 * A matrix map's configuration (doc 01 §12.8, data model §9 `maps.matrix`), for managers: the two axes (field, label,
 * min, max, split line), the bubble size field, the four quadrants' labels, and which items appear – types, statuses,
 * project templates, shared with groups. Saved as one change of `matrix`; the cards move at once.
 */
const props = defineProps<{ matrix: MatrixSettings | null; save: (matrix: MatrixSettings) => Promise<void> }>()
const visible = defineModel<boolean>('visible', { default: false })
const notify = useNotify()
const statuses = useStatuses()
const groups = useGroups()

const fields = useLive(
  async () =>
    ((await localDb().synced('field_definitions').toArray()) as unknown as FieldDefinitionRecord[]).filter(
      (f) => !f.deleted,
    ),
  [] as FieldDefinitionRecord[],
)
const templates = useLive(
  async () =>
    ((await localDb().synced('project_templates').toArray()) as unknown as ProjectTemplateRow[]).filter(
      (t) => !t.deleted && !t.isArchived,
    ),
  [] as ProjectTemplateRow[],
)
const axes = computed(() => axisFields(fields.value.value))

interface AxisDraft {
  field: string | null
  label: string
  min: number
  max: number
  split: number | null
}
const x = reactive<AxisDraft>({ field: null, label: '', min: 0, max: 100, split: null })
const y = reactive<AxisDraft>({ field: null, label: '', min: 0, max: 100, split: null })
const drafts = [
  { id: 'x', name: 'Horizontal axis', draft: x },
  { id: 'y', name: 'Vertical axis', draft: y },
]
const size = ref<string | null>(null)
const quadrants = ref<Record<Quadrant, string>>({ ...DEFAULT_QUADRANT_LABELS })
const types = ref<ItemType[]>([])
const statusIds = ref<string[]>([])
const templateIds = ref<string[]>([])
const groupIds = ref<string[]>([])
const saving = ref(false)
const problem = ref<string | null>(null)

watch(
  visible,
  (open) => {
    if (!open) return
    const m = props.matrix
    const axis = (a: MatrixSettings['x'] | undefined): AxisDraft => ({
      field: a?.field ?? null,
      label: a?.label ?? '',
      min: a?.min ?? 0,
      max: a?.max ?? 100,
      split: a?.split ?? null,
    })
    Object.assign(x, axis(m?.x))
    Object.assign(y, axis(m?.y))
    size.value = m?.size?.field ?? null
    quadrants.value = { ...DEFAULT_QUADRANT_LABELS, ...(m?.quadrants ?? {}) } as Record<Quadrant, string>
    types.value = [...(m?.filter?.types ?? [])]
    statusIds.value = [...(m?.filter?.statusIds ?? [])]
    templateIds.value = [...(m?.filter?.templateIds ?? [])]
    groupIds.value = [...(m?.filter?.groupIds ?? [])]
    problem.value = null
  },
  { immediate: true },
)

/** A newly chosen field brings its range and label. */
function pickField(draft: AxisDraft, key: string | null) {
  const field = axes.value.find((a) => a.key === key)
  draft.field = key
  if (field) {
    draft.min = field.min
    draft.max = field.max
    draft.split = null
    if (!draft.label || axes.value.some((a) => a.label === draft.label)) draft.label = field.label
  }
}

const typeOptions = (['note', 'idea', 'lead', 'project'] as ItemType[]).map((t) => ({
  value: t,
  label: itemTypes[t].plural,
}))
const statusOptions = computed(() =>
  statuses.all.value
    .filter((s) => !types.value.length || types.value.includes(s.itemType))
    .map((s) => ({ value: s.id, label: `${s.name} (${itemTypes[s.itemType].label})` })),
)
const quadrantOrder: { key: Quadrant; label: string }[] = [
  { key: 'tl', label: 'Top left' },
  { key: 'tr', label: 'Top right' },
  { key: 'bl', label: 'Bottom left' },
  { key: 'br', label: 'Bottom right' },
]

async function submit() {
  if (!x.field || !y.field) {
    problem.value = 'Both axes need a field.'
    return
  }
  if (x.min === x.max || y.min === y.max) {
    problem.value = 'An axis needs a range: min and max differ.'
    return
  }
  const axis = (a: AxisDraft) => ({
    field: a.field!,
    ...(a.label.trim() ? { label: a.label.trim() } : {}),
    min: a.min,
    max: a.max,
    ...(a.split !== null ? { split: a.split } : {}),
  })
  const matrix: MatrixSettings = {
    x: axis(x),
    y: axis(y),
    size: size.value ? { field: size.value } : null,
    quadrants: Object.fromEntries(
      quadrantOrder.map((q) => [q.key, quadrants.value[q.key]?.trim() || DEFAULT_QUADRANT_LABELS[q.key]]),
    ) as Record<Quadrant, string>,
    filter: {
      types: types.value.length ? types.value : null,
      statusIds: statusIds.value,
      templateIds: templateIds.value,
      groupIds: groupIds.value,
    },
  }
  saving.value = true
  try {
    await props.save(matrix)
    visible.value = false
  } catch (e) {
    notify.error(e, 'Matrix not changed')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="Matrix settings" modal :style="{ width: 'min(40rem, 100vw)' }">
    <form id="matrix-config" class="form" novalidate @submit.prevent="submit">
      <fieldset v-for="{ id: name, name: legend, draft } in drafts" :key="name" class="axis">
        <legend>{{ legend }}</legend>
        <div class="grid">
          <div class="field wide">
            <label :id="`mc-${name}-field-label`" :for="`mc-${name}-field`">Field</label>
            <Select
              :model-value="draft.field"
              :input-id="`mc-${name}-field`"
              :aria-labelledby="`mc-${name}-field-label`"
              :options="axes"
              option-label="label"
              option-value="key"
              placeholder="A number, rating or score"
              @update:model-value="(k: string | null) => pickField(draft, k)"
            />
          </div>
          <div class="field wide">
            <label :for="`mc-${name}-label`">Label</label>
            <InputText :id="`mc-${name}-label`" v-model="draft.label" maxlength="100" />
          </div>
          <div class="field">
            <label :for="`mc-${name}-min`">Min</label>
            <InputNumber v-model="draft.min" :input-id="`mc-${name}-min`" :max-fraction-digits="4" />
          </div>
          <div class="field">
            <label :for="`mc-${name}-max`">Max</label>
            <InputNumber v-model="draft.max" :input-id="`mc-${name}-max`" :max-fraction-digits="4" />
          </div>
          <div class="field">
            <label :for="`mc-${name}-split`">Split</label>
            <InputNumber
              v-model="draft.split"
              :input-id="`mc-${name}-split`"
              :max-fraction-digits="4"
              :placeholder="String((draft.min + draft.max) / 2)"
            />
          </div>
        </div>
      </fieldset>
      <div class="field">
        <label id="mc-size-label" for="mc-size">Bubble size</label>
        <Select
          v-model="size"
          input-id="mc-size"
          aria-labelledby="mc-size-label"
          :options="axes"
          option-label="label"
          option-value="key"
          show-clear
          placeholder="All cards the same size"
        />
      </div>
      <fieldset class="quadrants">
        <legend>Quadrants</legend>
        <div class="grid two">
          <div v-for="q in quadrantOrder" :key="q.key" class="field">
            <label :for="`mc-q-${q.key}`">{{ q.label }}</label>
            <InputText :id="`mc-q-${q.key}`" v-model="quadrants[q.key]" maxlength="100" />
          </div>
        </div>
      </fieldset>
      <fieldset>
        <legend>Which items appear</legend>
        <div class="grid two">
          <div class="field">
            <label id="mc-types-label" for="mc-types">Types</label>
            <MultiSelect
              v-model="types"
              input-id="mc-types"
              aria-labelledby="mc-types-label"
              :options="typeOptions"
              option-label="label"
              option-value="value"
              placeholder="All types"
            />
          </div>
          <div class="field">
            <label id="mc-statuses-label" for="mc-statuses">Statuses</label>
            <MultiSelect
              v-model="statusIds"
              input-id="mc-statuses"
              aria-labelledby="mc-statuses-label"
              :options="statusOptions"
              option-label="label"
              option-value="value"
              filter
              placeholder="Any status"
            />
          </div>
          <div class="field">
            <label id="mc-templates-label" for="mc-templates">Project templates</label>
            <MultiSelect
              v-model="templateIds"
              input-id="mc-templates"
              aria-labelledby="mc-templates-label"
              :options="templates.value.value"
              option-label="name"
              option-value="id"
              placeholder="Any template"
            />
          </div>
          <div class="field">
            <label id="mc-groups-label" for="mc-groups">Shared with</label>
            <MultiSelect
              v-model="groupIds"
              input-id="mc-groups"
              aria-labelledby="mc-groups-label"
              :options="groups"
              option-label="name"
              option-value="id"
              filter
              placeholder="Any group"
            />
          </div>
        </div>
      </fieldset>
      <small v-if="problem" class="field-error">{{ problem }}</small>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="matrix-config" label="Save" icon="pi pi-check" :loading="saving" />
    </template>
  </Dialog>
</template>

<style scoped>
fieldset {
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pgpm-space-2) var(--pgpm-space-3) var(--pgpm-space-3);
  margin: 0;
}

legend {
  font-weight: 600;
  padding: 0 var(--pgpm-space-1);
}

.grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: var(--pgpm-space-2);
}

.grid.two {
  grid-template-columns: 1fr 1fr;
}

.grid .wide {
  grid-column: span 3;
}

.grid :deep(.p-inputnumber input) {
  width: 100%;
}
</style>
