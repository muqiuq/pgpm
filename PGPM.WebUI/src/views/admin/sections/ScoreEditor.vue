<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { inputMax, NA_MODES, SCORE_OPS, type FieldDef, type ScoreConfigError } from '@/domain/score'
import type { FieldDefinitionRow } from '@/local/rows'
import {
  cappedMax,
  naLabels,
  opLabels,
  scoreErrorMessage,
  scoreInputCandidates,
  scorePreview,
  typeLabel,
  type ScoreForm,
} from './fieldConfig'

/**
 * A score's configuration (docs/01 §6.1, data model §7.4, docs/03 §16): the inputs (numeric fields of the catalogue,
 * each with its maximum and an optional cap), the operation, how *n/a* answers count, the maximum, scale, normalisation
 * to 0–100 and decimals – with what it will show ("… / 65") and the validation's messages per input. v-model is the
 * form; the dialog validates with `validateScoreConfig` before saving.
 */
const props = withDefaults(
  defineProps<{
    fields: readonly FieldDefinitionRow[]
    ownKey: string
    errors?: ScoreConfigError[]
    disabled?: boolean
  }>(),
  { errors: () => [], disabled: false },
)
const form = defineModel<ScoreForm>({ required: true })

const defsByKey = computed<Record<string, FieldDef>>(() =>
  Object.fromEntries(
    props.fields.map((f) => [f.key, { key: f.key, type: f.type, config: f.config, isArchived: f.isArchived }]),
  ),
)
const byKey = computed(() => new Map(props.fields.map((f) => [f.key, f])))
const label = (key: string) => byKey.value.get(key)?.label ?? key

const candidates = computed(() =>
  scoreInputCandidates(
    props.fields.map((f) => ({ key: f.key, type: f.type, config: f.config, isArchived: f.isArchived, label: f.label })),
    props.ownKey,
  )
    .filter((d) => !form.value.inputs.includes(d.key))
    .map((d) => {
      const max = inputMax(d)
      return { key: d.key, label: `${d.label} (${d.key}) · ${max === null ? 'no max.' : `max. ${max}`}` }
    }),
)
const ops = SCORE_OPS.map((op) => ({ value: op, label: opLabels[op] }))
const naModes = NA_MODES.map((na) => ({ value: na, label: naLabels[na] }))
const preview = computed(() => scorePreview(form.value, defsByKey.value))
const inputErrors = (key: string) => props.errors.filter((e) => e.input === key).map((e) => scoreErrorMessage(e, label))
const general = computed(() => props.errors.filter((e) => !e.input || !form.value.inputs.includes(e.input)))

function set(change: Partial<ScoreForm>) {
  form.value = { ...form.value, ...change }
}

function add(key: string | null) {
  if (key && !form.value.inputs.includes(key)) set({ inputs: [...form.value.inputs, key] })
}

function remove(key: string) {
  const caps = { ...form.value.caps }
  delete caps[key]
  set({ inputs: form.value.inputs.filter((k) => k !== key), caps })
}

function move(i: number, step: -1 | 1) {
  const list = [...form.value.inputs]
  const j = i + step
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j]!, list[i]!]
  set({ inputs: list })
}

function maxOf(key: string): string {
  const def = defsByKey.value[key]
  if (!def) return 'unknown field'
  const max = cappedMax(def, form.value.caps[key])
  return max === null ? 'no max.' : `max. ${max}`
}
</script>

<template>
  <div class="score-editor">
    <div class="field">
      <span class="label">Inputs</span>
      <ol v-if="form.inputs.length" class="inputs">
        <li v-for="(key, i) in form.inputs" :key="key" :data-testid="`input-${key}`">
          <div class="line">
            <span class="order">
              <Button
                icon="pi pi-angle-up"
                text
                size="small"
                severity="secondary"
                :disabled="disabled || i === 0"
                :aria-label="`Move ${label(key)} up`"
                @click="move(i, -1)"
              />
              <Button
                icon="pi pi-angle-down"
                text
                size="small"
                severity="secondary"
                :disabled="disabled || i === form.inputs.length - 1"
                :aria-label="`Move ${label(key)} down`"
                @click="move(i, 1)"
              />
            </span>
            <span class="name ellipsis">{{ label(key) }}</span>
            <small class="muted mono">{{ key }}</small>
            <small class="muted">{{ byKey.get(key) ? typeLabel(byKey.get(key)!.type) : '' }} · {{ maxOf(key) }}</small>
            <InputNumber
              :model-value="form.caps[key] ?? null"
              size="small"
              class="cap"
              placeholder="Cap"
              :aria-label="`Cap of ${label(key)}`"
              :max-fraction-digits="4"
              :disabled="disabled"
              @update:model-value="(v: number | null) => set({ caps: { ...form.caps, [key]: v } })"
            />
            <Button
              icon="pi pi-times"
              text
              size="small"
              severity="secondary"
              :disabled="disabled"
              :aria-label="`Remove input ${label(key)}`"
              @click="remove(key)"
            />
          </div>
          <small v-for="m in inputErrors(key)" :key="m" class="field-error">{{ m }}</small>
        </li>
      </ol>
      <Select
        :model-value="null"
        :options="candidates"
        option-label="label"
        option-value="key"
        placeholder="Add an input…"
        filter
        size="small"
        aria-label="Add an input"
        :disabled="disabled"
        @update:model-value="add"
      />
    </div>
    <div class="form-grid">
      <div class="field">
        <label id="score-op-label" for="score-op">Operation</label>
        <Select
          :model-value="form.op"
          input-id="score-op"
          aria-labelledby="score-op-label"
          :options="ops"
          option-label="label"
          option-value="value"
          :disabled="disabled"
          @update:model-value="(v) => set({ op: v })"
        />
      </div>
      <div class="field">
        <label id="score-na-label" for="score-na">n/a answers</label>
        <Select
          :model-value="form.na"
          input-id="score-na"
          aria-labelledby="score-na-label"
          :options="naModes"
          option-label="label"
          option-value="value"
          :disabled="disabled"
          @update:model-value="(v) => set({ na: v })"
        />
      </div>
      <div class="field">
        <label for="score-max">Maximum</label>
        <InputNumber
          :model-value="form.max"
          input-id="score-max"
          :max-fraction-digits="4"
          placeholder="None"
          :disabled="disabled"
          @update:model-value="(v: number | null) => set({ max: v })"
        />
      </div>
      <div class="field">
        <label for="score-scale">Scale</label>
        <InputNumber
          :model-value="form.scale"
          input-id="score-scale"
          :max-fraction-digits="6"
          placeholder="1"
          :disabled="disabled"
          @update:model-value="(v: number | null) => set({ scale: v })"
        />
      </div>
      <div class="field">
        <label for="score-decimals">Decimals</label>
        <InputNumber
          :model-value="form.decimals"
          input-id="score-decimals"
          :min="0"
          :max="10"
          :use-grouping="false"
          :disabled="disabled"
          @update:model-value="(v: number | null) => set({ decimals: v ?? 0 })"
        />
      </div>
      <div class="switch">
        <ToggleSwitch
          :model-value="form.normalize"
          input-id="score-normalize"
          :disabled="disabled"
          @update:model-value="(v: boolean) => set({ normalize: v })"
        />
        <label for="score-normalize">Normalise to 0–100</label>
      </div>
    </div>
    <p class="muted preview" data-testid="score-preview">{{ preview }}</p>
    <ul v-if="general.length" class="errors" role="alert">
      <li v-for="e in general" :key="`${e.code}-${e.input ?? ''}`" class="field-error">
        {{ scoreErrorMessage(e, label) }}
      </li>
    </ul>
  </div>
</template>

<style scoped>
.score-editor {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.label {
  font-weight: 500;
}

.inputs {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.inputs li {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid var(--pgpm-border);
  padding: 2px 0;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.order {
  display: inline-flex;
}

.name {
  min-width: 0;
  flex: 1;
}

.cap {
  width: 5.5rem;
  flex: none;
}

.cap :deep(input) {
  width: 100%;
}

.switch {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.preview {
  margin: 0;
}

.errors {
  margin: 0;
  padding-left: 1rem;
}
</style>
