<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { pickableOptions } from '@/domain/fields/fieldWidget'
import { optionPart } from '@/domain/fields/renderValue'
import type { Catalogue } from '@/local/catalogue'
import type { FieldDefinitionRow, ItemType } from '@/local/rows'
import { fieldColumns, type FieldFilter } from '@/views/items/itemList'

/**
 * The dynamic field filters of an idea or project list (docs/01 §4.3, `viewFilters.fields`): text contains,
 * number/rating/score/date range, select in, yes/no, has a value or not. The filters a saved view brings show here and
 * can be removed; *Add* builds one. v-model is the list of filters (`field:<key>`).
 */
const props = defineProps<{ type: ItemType; catalogue: Catalogue }>()
const filters = defineModel<FieldFilter[]>({ default: () => [] })

type Op = FieldFilter['op']
const opLabels: Record<Op, string> = {
  eq: 'is',
  in: 'is one of',
  contains: 'contains',
  range: 'between',
  set: 'has a value',
  unset: 'has no value',
}

const fields = computed(() => fieldColumns(props.type, props.catalogue).map((c) => c.field!))
const fieldOf = (id: string) => props.catalogue.fieldByKey.get(id.replace(/^field:/, ''))

function opsFor(field: FieldDefinitionRow | undefined): Op[] {
  switch (field?.type) {
    case 'number':
    case 'rating':
      return ['range', 'eq', 'set', 'unset']
    case 'score':
    case 'date':
    case 'datetime':
      return ['range', 'set', 'unset']
    case 'bool':
      return ['eq']
    case 'select':
    case 'multiselect':
      return ['in', 'set', 'unset']
    case 'person':
      return ['contains', 'set', 'unset']
    default:
      return ['contains', 'set', 'unset']
  }
}

function describe(f: FieldFilter): string {
  const field = fieldOf(f.field)
  const name = field?.label ?? f.field.replace(/^field:/, '')
  switch (f.op) {
    case 'eq':
      return `${name} is ${field?.type === 'bool' ? (f.value === true ? 'yes' : 'no') : String(f.value)}`
    case 'in':
      return `${name} is ${(f.values ?? []).map((id) => optionPart(field?.config, id).label).join(' or ')}`
    case 'contains':
      return `${name} contains “${String(f.value ?? '')}”`
    case 'range':
      return `${name} ${f.min !== undefined ? `≥ ${f.min}` : ''}${f.min !== undefined && f.max !== undefined ? ' and ' : ''}${f.max !== undefined ? `≤ ${f.max}` : ''}`
    default:
      return `${name} ${opLabels[f.op]}`
  }
}

function remove(index: number) {
  filters.value = filters.value.filter((_, i) => i !== index)
}

// --- the filter being added ---
const fieldKey = ref<string | null>(null)
const op = ref<Op>('contains')
const text = ref('')
const min = ref('')
const max = ref('')
const yes = ref(true)
const values = ref<string[]>([])
const current = computed(() => (fieldKey.value ? props.catalogue.fieldByKey.get(fieldKey.value) : undefined))
const ops = computed(() => opsFor(current.value).map((value) => ({ value, label: opLabels[value] })))
const dateLike = computed(() => current.value?.type === 'date' || current.value?.type === 'datetime')
const choices = computed(() =>
  pickableOptions(current.value?.config).map((o) => ({ value: o.id, label: o.label ?? o.id })),
)
watch(current, (field) => {
  op.value = opsFor(field)[0]!
  text.value = ''
  min.value = ''
  max.value = ''
  values.value = []
  yes.value = true
})

const ready = computed(() => {
  if (!current.value) return false
  switch (op.value) {
    case 'contains':
      return !!text.value.trim()
    case 'in':
      return values.value.length > 0
    case 'range':
      return !!min.value.trim() || !!max.value.trim()
    case 'eq':
      return current.value.type === 'bool' || text.value.trim() !== ''
    default:
      return true
  }
})

function add() {
  if (!ready.value || !current.value) return
  const filter: FieldFilter = { field: `field:${current.value.key}`, op: op.value }
  const bound = (v: string) => (dateLike.value ? v : Number(v))
  if (op.value === 'contains') filter.value = text.value.trim()
  if (op.value === 'in') filter.values = [...values.value]
  if (op.value === 'eq') filter.value = current.value.type === 'bool' ? yes.value : Number(text.value)
  if (op.value === 'range') {
    if (min.value.trim()) filter.min = bound(min.value.trim())
    if (max.value.trim()) filter.max = bound(max.value.trim())
  }
  filters.value = [...filters.value, filter]
  fieldKey.value = null
}
</script>

<template>
  <div class="field-filters" role="group" aria-label="Field filters">
    <span class="label">Fields</span>
    <ul v-if="filters.length" class="active">
      <li v-for="(f, i) in filters" :key="`${f.field}-${i}`">
        <span>{{ describe(f) }}</span>
        <Button
          icon="pi pi-times"
          text
          rounded
          size="small"
          severity="secondary"
          :aria-label="`Remove the filter ${describe(f)}`"
          @click="remove(i)"
        />
      </li>
    </ul>
    <div class="new">
      <Select
        v-model="fieldKey"
        :options="fields"
        option-label="label"
        option-value="key"
        filter
        placeholder="Add a field filter…"
        size="small"
        aria-label="Field"
      />
      <template v-if="current">
        <Select
          v-model="op"
          :options="ops"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Condition"
        />
        <InputText
          v-if="op === 'contains' || (op === 'eq' && current.type !== 'bool')"
          v-model="text"
          size="small"
          :type="op === 'eq' ? 'number' : 'text'"
          aria-label="Value"
        />
        <Select
          v-else-if="op === 'eq'"
          v-model="yes"
          :options="[
            { label: 'Yes', value: true },
            { label: 'No', value: false },
          ]"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Value"
        />
        <MultiSelect
          v-else-if="op === 'in'"
          v-model="values"
          :options="choices"
          option-label="label"
          option-value="value"
          size="small"
          placeholder="Options"
          aria-label="Options"
        />
        <div v-else-if="op === 'range'" class="range">
          <InputText v-model="min" size="small" :type="dateLike ? 'date' : 'number'" aria-label="From" />
          <span aria-hidden="true">–</span>
          <InputText v-model="max" size="small" :type="dateLike ? 'date' : 'number'" aria-label="To" />
        </div>
        <Button label="Add" icon="pi pi-plus" size="small" :disabled="!ready" @click="add" />
      </template>
    </div>
  </div>
</template>

<style scoped>
.field-filters {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.label {
  font-weight: 600;
}

.active {
  list-style: none;
  margin: 0;
  padding: 0;
}

.active li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
}

.new {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.range {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.range :deep(input) {
  flex: 1;
  min-width: 0;
}
</style>
