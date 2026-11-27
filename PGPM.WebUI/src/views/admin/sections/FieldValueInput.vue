<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed } from 'vue'
import {
  dateTimeFromLocal,
  dateTimeToLocal,
  pickableOptions,
  ratingScale,
  type FieldLike,
} from '@/domain/fields/fieldWidget'

/**
 * A value of a field in the configuration – a field's default value, a template's field default (docs/01 §6.1, §10.2) –
 * in the stored shape (`field_values.value`, data model §7.5): texts, numbers, dates, yes/no, options, ratings. Persons
 * and scores have no configured values. v-model is the value (null: none).
 */
const props = withDefaults(
  defineProps<{ field: FieldLike; inputId: string; disabled?: boolean; invalid?: boolean }>(),
  {
    disabled: false,
    invalid: false,
  },
)
const value = defineModel<unknown>()

const text = computed(() => (typeof value.value === 'string' ? value.value : ''))
const setText = (v: string | undefined) => (value.value = v ? v : null)
const options = computed(() =>
  pickableOptions(props.field.config, Array.isArray(value.value) ? value.value : [value.value]).map((o) => ({
    value: o.id,
    label: o.archived ? `${o.label ?? o.id} (archived)` : (o.label ?? o.id),
  })),
)
const ratings = computed(() => {
  const { min, max, allowNa, labels } = ratingScale(props.field.config)
  const list: { value: number | 'na'; label: string }[] = []
  for (let n = min; n <= max; n++)
    list.push({ value: n, label: labels[String(n)] ? `${n} – ${labels[String(n)]}` : String(n) })
  if (allowNa) list.push({ value: 'na', label: 'n/a' })
  return list
})
const yesNo = [
  { value: true, label: 'Yes' },
  { value: false, label: 'No' },
]
</script>

<template>
  <InputText
    v-if="field.type === 'text' || field.type === 'url' || field.type === 'email'"
    :id="inputId"
    :model-value="text"
    :type="field.type === 'email' ? 'email' : field.type === 'url' ? 'url' : 'text'"
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="setText"
  />
  <Textarea
    v-else-if="field.type === 'textarea' || field.type === 'markdown'"
    :id="inputId"
    :model-value="text"
    rows="3"
    auto-resize
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="setText"
  />
  <InputNumber
    v-else-if="field.type === 'number'"
    :model-value="typeof value === 'number' ? value : null"
    :input-id="inputId"
    :aria-labelledby="`${inputId}-label`"
    :max-fraction-digits="6"
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="(v: number | null) => (value = v)"
  />
  <InputText
    v-else-if="field.type === 'date'"
    :id="inputId"
    :model-value="text"
    type="date"
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="setText"
  />
  <InputText
    v-else-if="field.type === 'datetime'"
    :id="inputId"
    :model-value="dateTimeToLocal(value)"
    type="datetime-local"
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="(v) => (value = dateTimeFromLocal(v ?? ''))"
  />
  <Select
    v-else-if="field.type === 'bool'"
    v-model="value"
    :input-id="inputId"
    :aria-labelledby="`${inputId}-label`"
    :options="yesNo"
    option-label="label"
    option-value="value"
    placeholder="None"
    show-clear
    :disabled="disabled"
    :invalid="invalid"
  />
  <Select
    v-else-if="field.type === 'select'"
    v-model="value"
    :input-id="inputId"
    :aria-labelledby="`${inputId}-label`"
    :options="options"
    option-label="label"
    option-value="value"
    placeholder="None"
    show-clear
    :disabled="disabled"
    :invalid="invalid"
  />
  <MultiSelect
    v-else-if="field.type === 'multiselect'"
    :model-value="Array.isArray(value) ? value : []"
    :input-id="inputId"
    :aria-labelledby="`${inputId}-label`"
    :options="options"
    option-label="label"
    option-value="value"
    placeholder="None"
    display="chip"
    :disabled="disabled"
    :invalid="invalid"
    @update:model-value="(v: string[]) => (value = v.length ? v : null)"
  />
  <Select
    v-else-if="field.type === 'rating'"
    v-model="value"
    :input-id="inputId"
    :aria-labelledby="`${inputId}-label`"
    :options="ratings"
    option-label="label"
    option-value="value"
    placeholder="None"
    show-clear
    :disabled="disabled"
    :invalid="invalid"
  />
  <small v-else class="muted">{{ field.type === 'score' ? 'Computed, no value.' : 'Set on the item.' }}</small>
</template>
