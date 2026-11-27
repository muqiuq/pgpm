<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Popover from 'primevue/popover'
import { ref } from 'vue'
import ColorPicker from '@/components/items/ColorPicker.vue'
import { optionIdFromLabel, type OptionForm } from './fieldConfig'

/**
 * The options of a single or multi select (docs/01 §6.1, data model §7.4): label, a stable id (made from the label for
 * a new option; a saved option keeps its id, since values refer to it), colour, optional points (used by scores) and
 * archived. New options can be removed, saved ones only archived (their values stay valid). v-model is the list.
 */
withDefaults(defineProps<{ errors?: Record<string, string>; disabled?: boolean; idPrefix?: string }>(), {
  errors: () => ({}),
  disabled: false,
  idPrefix: 'opt',
})
const options = defineModel<OptionForm[]>({ required: true })

const colorPanel = ref<InstanceType<typeof Popover> | null>(null)
const coloring = ref<number | null>(null)

function update(i: number, change: Partial<OptionForm>) {
  options.value = options.value.map((o, j) => (j === i ? { ...o, ...change } : o))
}

const othersIds = (i: number) => options.value.filter((_, j) => j !== i).map((o) => o.id)

/** A new option's id follows its label until the id is changed by hand. */
function setLabel(i: number, label: string) {
  const row = options.value[i]!
  const followed = !row.saved && (row.id === '' || row.id === optionIdFromLabel(row.label, othersIds(i)))
  update(i, followed ? { label, id: optionIdFromLabel(label, othersIds(i)) } : { label })
}

function add() {
  options.value = [...options.value, { id: '', label: '', color: null, points: null, archived: false, saved: false }]
}

function remove(i: number) {
  options.value = options.value.filter((_, j) => j !== i)
}

function move(i: number, step: -1 | 1) {
  const list = [...options.value]
  const j = i + step
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j]!, list[i]!]
  options.value = list
}

function openColor(event: Event, i: number) {
  coloring.value = i
  colorPanel.value?.toggle(event)
}

function pickColor(color: string | null) {
  if (coloring.value !== null) update(coloring.value, { color })
  colorPanel.value?.hide()
}
</script>

<template>
  <div class="options-editor">
    <ol v-if="options.length" class="rows">
      <li v-for="(o, i) in options" :key="i" :class="{ archived: o.archived }" :data-testid="`option-${i}`">
        <span class="order">
          <Button
            icon="pi pi-angle-up"
            text
            size="small"
            severity="secondary"
            :disabled="disabled || i === 0"
            :aria-label="`Move option ${o.label || i + 1} up`"
            @click="move(i, -1)"
          />
          <Button
            icon="pi pi-angle-down"
            text
            size="small"
            severity="secondary"
            :disabled="disabled || i === options.length - 1"
            :aria-label="`Move option ${o.label || i + 1} down`"
            @click="move(i, 1)"
          />
        </span>
        <button
          type="button"
          class="swatch"
          :style="{ background: o.color ?? 'transparent' }"
          :aria-label="`Colour of option ${o.label || i + 1}`"
          :disabled="disabled"
          @click="openColor($event, i)"
        />
        <div class="cell label">
          <InputText
            :id="`${idPrefix}-label-${i}`"
            :model-value="o.label"
            size="small"
            placeholder="Label"
            maxlength="200"
            :aria-label="`Label of option ${i + 1}`"
            :invalid="!!errors[`${i}.label`]"
            :disabled="disabled"
            @update:model-value="(v) => setLabel(i, v ?? '')"
          />
          <small v-if="errors[`${i}.label`]" class="field-error">{{ errors[`${i}.label`] }}</small>
        </div>
        <div class="cell id">
          <InputText
            :id="`${idPrefix}-id-${i}`"
            :model-value="o.id"
            size="small"
            class="mono"
            placeholder="id"
            maxlength="63"
            :aria-label="`Id of option ${i + 1}`"
            :invalid="!!errors[`${i}.id`]"
            :disabled="disabled || o.saved"
            :title="o.saved ? 'The id of a saved option stays: values refer to it.' : undefined"
            @update:model-value="(v) => update(i, { id: v ?? '' })"
          />
          <small v-if="errors[`${i}.id`]" class="field-error">{{ errors[`${i}.id`] }}</small>
        </div>
        <InputNumber
          :model-value="o.points"
          size="small"
          class="points"
          placeholder="Points"
          :input-id="`${idPrefix}-points-${i}`"
          :aria-label="`Points of option ${i + 1}`"
          :max-fraction-digits="4"
          :disabled="disabled"
          @update:model-value="(v: number | null) => update(i, { points: v })"
        />
        <Button
          v-if="o.saved"
          :icon="o.archived ? 'pi pi-replay' : 'pi pi-folder'"
          text
          size="small"
          severity="secondary"
          :disabled="disabled"
          :aria-label="o.archived ? `Unarchive option ${o.label}` : `Archive option ${o.label}`"
          :title="o.archived ? 'Unarchive' : 'Archive: not offered any more, existing values stay'"
          @click="update(i, { archived: !o.archived })"
        />
        <Button
          v-else
          icon="pi pi-times"
          text
          size="small"
          severity="secondary"
          :disabled="disabled"
          :aria-label="`Remove option ${o.label || i + 1}`"
          @click="remove(i)"
        />
      </li>
    </ol>
    <small v-if="errors.options" class="field-error">{{ errors.options }}</small>
    <Button label="Add option" icon="pi pi-plus" size="small" text :disabled="disabled" @click="add" />
    <Popover ref="colorPanel">
      <ColorPicker
        :model-value="coloring === null ? null : (options[coloring]?.color ?? null)"
        :id-prefix="`${idPrefix}-color`"
        @change="pickColor"
      />
    </Popover>
  </div>
</template>

<style scoped>
.options-editor {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--pgpm-space-1);
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.rows li {
  display: flex;
  align-items: flex-start;
  gap: var(--pgpm-space-1);
}

.rows li.archived {
  opacity: 0.6;
}

.order {
  display: inline-flex;
}

.cell {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.label {
  flex: 2;
}

.id {
  flex: 1.3;
}

.points {
  width: 6rem;
  flex: none;
}

.points :deep(input) {
  width: 100%;
}

.swatch {
  flex: none;
  width: 20px;
  height: 20px;
  margin-top: 6px;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
  cursor: pointer;
  padding: 0;
}
</style>
