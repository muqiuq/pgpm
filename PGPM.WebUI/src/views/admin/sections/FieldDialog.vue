<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { fieldsApi, type FieldInput } from '@/api/config'
import { useNotify } from '@/composables/useNotify'
import { valueError } from '@/domain/fields/fieldWidget'
import { validateScoreConfig, type FieldDef, type FieldType, type ScoreConfigError } from '@/domain/score'
import type { FieldDefinitionRow, SectionRow } from '@/local/rows'
import FieldValueInput from './FieldValueInput.vue'
import OptionsEditor from './OptionsEditor.vue'
import ScoreEditor from './ScoreEditor.vue'
import {
  configErrors,
  configForm,
  configOf,
  FIELD_KEY_PATTERN,
  fieldTypes,
  keyFromLabel,
  scoreConfig,
  serverConfigMessage,
  type ConfigForm,
} from './fieldConfig'

/**
 * A field's settings (docs/01 §6.1, data model §7.4): section, label, key (on creation only – it is immutable, scores,
 * MCP and packages use it), help text, type (on creation only), required, a sub-heading and the type's configuration:
 * text length and placeholder, number decimals/min/max/unit, the timebox flag of dates, select options with colours and
 * points, multiple persons, the rating scale, the score editor. A score is validated with `validateScoreConfig` (docs/03
 * §16) before it is sent; the server checks again (`errors.config`).
 */
const props = withDefaults(
  defineProps<{
    field: FieldDefinitionRow | null
    sectionId: string | null
    sections: readonly SectionRow[]
    /** Every field of the catalogue (score inputs, keys taken). */
    fields: readonly FieldDefinitionRow[]
    disabled?: boolean
  }>(),
  { disabled: false },
)
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ saved: [] }>()
const notify = useNotify()

const section = ref<string | null>(null)
const label = ref('')
const key = ref('')
const keyTouched = ref(false)
const helpText = ref('')
const type = ref<FieldType>('text')
const required = ref(false)
const config = ref<ConfigForm>(configForm('text', null))
const defaultValue = ref<unknown>(null)
const errors = ref<Record<string, string | undefined>>({})
const scoreErrors = ref<ScoreConfigError[]>([])
const saving = ref(false)

watch(
  visible,
  (open) => {
    if (!open) return
    const f = props.field
    section.value = f?.sectionId ?? props.sectionId
    label.value = f?.label ?? ''
    key.value = f?.key ?? ''
    keyTouched.value = !!f
    helpText.value = f?.helpText ?? ''
    type.value = f?.type ?? 'text'
    required.value = f?.required ?? false
    config.value = configForm(type.value, f?.config)
    defaultValue.value = f?.defaultValue ?? null
    errors.value = {}
    scoreErrors.value = []
  },
  { immediate: true },
)

watch(label, (text) => {
  if (!props.field && !keyTouched.value) key.value = keyFromLabel(text)
})
watch(type, () => {
  if (!props.field) defaultValue.value = null
})

const sectionOptions = computed(() =>
  props.sections.filter((s) => !s.isArchived || s.id === section.value).map((s) => ({ value: s.id, label: s.name })),
)
const typeOptions = fieldTypes.map((t) => ({ value: t.value, label: t.label }))
const title = computed(() => (props.field ? `Edit field ${props.field.label}` : 'New field'))
const ownKey = computed(() => props.field?.key ?? key.value)
const labelOf = (k: string) => props.fields.find((f) => f.key === k)?.label ?? k
const hasDefault = computed(() => !['score', 'person'].includes(type.value))
const multiPointOptions = [
  { value: 'count', label: 'Count' },
  { value: 'sum', label: 'Sum of points' },
]

/** The definition being saved, as score.ts and the value rules read it. */
const draft = computed(() => ({
  key: ownKey.value,
  label: label.value,
  type: type.value,
  config: configOf(type.value, config.value),
  required: false,
}))

const ratingValues = computed(() => {
  const list: number[] = []
  const { ratingMin, ratingMax } = config.value
  if (Number.isInteger(ratingMin) && Number.isInteger(ratingMax) && ratingMax - ratingMin <= 20)
    for (let n = ratingMin; n <= ratingMax; n++) list.push(n)
  return list
})

function validate(): boolean {
  const next: Record<string, string | undefined> = {}
  if (!section.value) next.sectionId = 'Choose a section.'
  const text = label.value.trim()
  if (!text) next.label = 'A label is needed.'
  else if (text.length > 200) next.label = 'At most 200 characters.'
  if (!props.field) {
    if (!FIELD_KEY_PATTERN.test(key.value))
      next.key = 'Lower case letters, digits and _, starting with a letter (2–63).'
    else if (props.fields.some((f) => f.key === key.value)) next.key = 'This key is taken.'
  }
  for (const [member, message] of Object.entries(configErrors(type.value, config.value)))
    next[`config.${member}`] = message
  if (hasDefault.value && defaultValue.value !== null) {
    const problem = valueError(draft.value, defaultValue.value)
    if (problem) next.defaultValue = problem
  }
  scoreErrors.value =
    type.value === 'score'
      ? validateScoreConfig(
          { key: ownKey.value, type: 'score', config: scoreConfig(config.value.score) },
          props.fields.map((f): FieldDef => ({ key: f.key, type: f.type, config: f.config, isArchived: f.isArchived })),
        )
      : []
  errors.value = next
  return !Object.values(next).some(Boolean) && !scoreErrors.value.length
}

const optionErrors = computed(() =>
  Object.fromEntries(
    Object.entries(errors.value)
      .filter(([k, v]) => k.startsWith('config.options.') && v)
      .map(([k, v]) => [k.slice('config.options.'.length), v as string]),
  ),
)

async function save() {
  if (props.disabled || !validate()) return
  saving.value = true
  const body: FieldInput = {
    sectionId: section.value!,
    label: label.value.trim(),
    helpText: helpText.value.trim() || null,
    config: configOf(type.value, config.value),
    required: required.value,
    defaultValue: hasDefault.value ? defaultValue.value : null,
    ...(props.field ? {} : { key: key.value, type: type.value }),
  }
  try {
    if (props.field) await fieldsApi.update(props.field.id, body)
    else await fieldsApi.create(body)
    notify.success(props.field ? 'Field saved' : 'Field created')
    visible.value = false
    emit('saved')
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      const serverConfig = e.errors.config ?? []
      errors.value = {
        sectionId: e.field('sectionId'),
        label: e.field('label'),
        key: e.code === 'duplicate_key' ? (e.field('key') ?? 'This key is taken.') : e.field('key'),
        helpText: e.field('helpText'),
        type: e.field('type'),
        defaultValue: e.field('defaultValue'),
        config: serverConfig.map((m) => serverConfigMessage(m, labelOf)).join(' ') || undefined,
      }
      if (!Object.values(errors.value).some(Boolean)) notify.error(e, 'Not saved')
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}

function setLabel(n: number, text: string) {
  config.value = { ...config.value, labels: { ...config.value.labels, [String(n)]: text } }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="title" modal :style="{ width: 'min(46rem, 100vw)' }">
    <form id="field-form" class="form" novalidate @submit.prevent="save">
      <div class="form-grid">
        <div class="field">
          <label for="fd-label">Label</label>
          <InputText id="fd-label" v-model="label" maxlength="200" autofocus :invalid="!!errors.label" />
          <small v-if="errors.label" class="field-error">{{ errors.label }}</small>
        </div>
        <div class="field">
          <label for="fd-key">Key</label>
          <InputText
            id="fd-key"
            v-model="key"
            class="mono"
            maxlength="63"
            :disabled="!!field"
            :invalid="!!errors.key"
            @input="keyTouched = true"
          />
          <small v-if="errors.key" class="field-error">{{ errors.key }}</small>
          <small v-else class="field-hint">{{
            field
              ? 'The key never changes: scores, MCP and packages use it.'
              : 'Stable technical name; can’t be changed later.'
          }}</small>
        </div>
        <div class="field">
          <label id="fd-type-label" for="fd-type">Type</label>
          <Select
            v-model="type"
            input-id="fd-type"
            aria-labelledby="fd-type-label"
            :options="typeOptions"
            option-label="label"
            option-value="value"
            :disabled="!!field"
            @update:model-value="(t: FieldType) => (config = configForm(t, null))"
          />
          <small v-if="errors.type" class="field-error">{{ errors.type }}</small>
        </div>
        <div class="field">
          <label id="fd-section-label" for="fd-section">Section</label>
          <Select
            v-model="section"
            input-id="fd-section"
            aria-labelledby="fd-section-label"
            :options="sectionOptions"
            option-label="label"
            option-value="value"
            :invalid="!!errors.sectionId"
          />
          <small v-if="errors.sectionId" class="field-error">{{ errors.sectionId }}</small>
        </div>
        <div class="field wide">
          <label for="fd-help">Help text</label>
          <Textarea id="fd-help" v-model="helpText" rows="2" auto-resize maxlength="2000" />
          <small v-if="errors.helpText" class="field-error">{{ errors.helpText }}</small>
        </div>
        <div class="field">
          <label for="fd-subheading">Sub-heading</label>
          <InputText id="fd-subheading" v-model="config.subheading" maxlength="100" placeholder="None" />
          <small v-if="errors['config.subheading']" class="field-error">{{ errors['config.subheading'] }}</small>
          <small v-else class="field-hint">Starts a group of fields within the section.</small>
        </div>
        <div v-if="type !== 'score'" class="switch">
          <ToggleSwitch v-model="required" input-id="fd-required" />
          <label for="fd-required">Required</label>
        </div>
      </div>

      <fieldset class="config" data-testid="field-config">
        <legend>{{ fieldTypes.find((t) => t.value === type)?.label }}</legend>
        <div v-if="type === 'text'" class="form-grid">
          <div class="field">
            <label for="fd-maxlength">Max. length</label>
            <InputNumber
              v-model="config.maxLength"
              input-id="fd-maxlength"
              :min="1"
              :max="10000"
              :use-grouping="false"
              placeholder="10,000"
            />
            <small v-if="errors['config.maxLength']" class="field-error">{{ errors['config.maxLength'] }}</small>
          </div>
          <div class="field">
            <label for="fd-placeholder">Placeholder</label>
            <InputText id="fd-placeholder" v-model="config.placeholder" maxlength="200" />
          </div>
        </div>
        <div v-else-if="type === 'number'" class="form-grid">
          <div class="field">
            <label for="fd-decimals">Decimals</label>
            <InputNumber v-model="config.decimals" input-id="fd-decimals" :min="0" :max="6" placeholder="As typed" />
            <small v-if="errors['config.decimals']" class="field-error">{{ errors['config.decimals'] }}</small>
          </div>
          <div class="field">
            <label for="fd-unit">Unit</label>
            <InputText id="fd-unit" v-model="config.unit" maxlength="40" placeholder="CHF, h, %" />
          </div>
          <div class="field">
            <label for="fd-min">Minimum</label>
            <InputNumber v-model="config.min" input-id="fd-min" :max-fraction-digits="6" placeholder="None" />
          </div>
          <div class="field">
            <label for="fd-max">Maximum</label>
            <InputNumber v-model="config.max" input-id="fd-max" :max-fraction-digits="6" placeholder="None" />
            <small v-if="errors['config.max']" class="field-error">{{ errors['config.max'] }}</small>
          </div>
        </div>
        <div v-else-if="type === 'date' || type === 'datetime'" class="switch">
          <ToggleSwitch v-model="config.timebox" input-id="fd-timebox" />
          <label for="fd-timebox">Timebox: overdue when passed while the item isn’t closed</label>
        </div>
        <div v-else-if="type === 'select' || type === 'multiselect'" class="form">
          <OptionsEditor v-model="config.options" :errors="optionErrors" id-prefix="fd-opt" />
          <div v-if="type === 'multiselect'" class="field">
            <label for="fd-multipoints">In scores</label>
            <SelectButton
              v-model="config.multiPoints"
              :options="multiPointOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              size="small"
              aria-labelledby="fd-multipoints"
            />
            <small id="fd-multipoints" class="field-hint">Count the chosen options, or add up their points.</small>
          </div>
        </div>
        <div v-else-if="type === 'person'" class="switch">
          <ToggleSwitch v-model="config.multiple" input-id="fd-multiple" />
          <label for="fd-multiple">Several persons</label>
        </div>
        <div v-else-if="type === 'rating'" class="form">
          <div class="form-grid">
            <div class="field">
              <label for="fd-rmin">From</label>
              <InputNumber v-model="config.ratingMin" input-id="fd-rmin" :use-grouping="false" />
            </div>
            <div class="field">
              <label for="fd-rmax">To</label>
              <InputNumber v-model="config.ratingMax" input-id="fd-rmax" :use-grouping="false" />
              <small v-if="errors['config.ratingMax']" class="field-error">{{ errors['config.ratingMax'] }}</small>
            </div>
          </div>
          <div class="switch">
            <ToggleSwitch v-model="config.allowNa" input-id="fd-na" />
            <label for="fd-na"><em>n/a</em> allowed</label>
          </div>
          <div v-if="ratingValues.length" class="labels">
            <span class="legend">Labels</span>
            <div v-for="n in ratingValues" :key="n" class="rating-label">
              <label :for="`fd-rl-${n}`" class="mono">{{ n }}</label>
              <InputText
                :id="`fd-rl-${n}`"
                :model-value="config.labels[String(n)] ?? ''"
                size="small"
                maxlength="60"
                placeholder="No label"
                @update:model-value="(v) => setLabel(n, v ?? '')"
              />
            </div>
          </div>
        </div>
        <ScoreEditor
          v-else-if="type === 'score'"
          v-model="config.score"
          :fields="fields"
          :own-key="ownKey"
          :errors="scoreErrors"
        />
        <p v-else class="muted none">No settings.</p>
        <small v-if="errors.config" class="field-error">{{ errors.config }}</small>
      </fieldset>

      <div v-if="hasDefault" class="field">
        <label id="fd-default-label" for="fd-default">Default value</label>
        <FieldValueInput v-model="defaultValue" :field="draft" input-id="fd-default" :invalid="!!errors.defaultValue" />
        <small v-if="errors.defaultValue" class="field-error">{{ errors.defaultValue }}</small>
        <small v-else class="field-hint">Filled in when the section is added to an item.</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="field-form"
        :label="field ? 'Save' : 'Create field'"
        :loading="saving"
        :disabled="disabled"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.switch {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.config {
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pgpm-space-3);
  margin: 0;
  min-width: 0;
}

.config legend {
  padding: 0 var(--pgpm-space-1);
  font-weight: 500;
}

.labels {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.legend {
  font-weight: 500;
}

.rating-label {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.rating-label label {
  width: 1.5rem;
  text-align: right;
}

.none {
  margin: 0;
}
</style>
