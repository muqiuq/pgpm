<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import { commonStyleKeys, DEFAULT_STYLES } from '../styles'
import type { ElementRow, ElementStyle } from '../types'
import ColorField from './ColorField.vue'

/**
 * Style properties of shapes, lines, texts and images (doc 01 §12.5), of one element or the common ones of a
 * multi-selection; every change is saved at once (one undoable step). A value the elements don't share shows empty.
 */
const props = defineProps<{ elements: readonly ElementRow[]; disabled: boolean }>()
const emit = defineEmits<{ change: [style: Partial<ElementStyle>] }>()

const keys = computed(() => new Set(commonStyleKeys(props.elements.map((e) => e.kind))))

/** The elements' shared value of a key (their own, else their kind's default); undefined when they differ. */
function value<K extends keyof ElementStyle>(key: K): ElementStyle[K] | undefined {
  let first: ElementStyle[K] | undefined
  for (const [i, el] of props.elements.entries()) {
    const v = el.style?.[key] ?? DEFAULT_STYLES[el.kind]?.[key]
    if (i === 0) first = v
    else if (v !== first) return undefined
  }
  return first
}

const set = (key: keyof ElementStyle, v: unknown) => emit('change', { [key]: v ?? undefined })

const dashes = [
  { label: 'Solid', value: 'solid' },
  { label: 'Dashed', value: 'dashed' },
  { label: 'Dotted', value: 'dotted' },
]
const aligns = [
  { icon: 'pi pi-align-left', value: 'left', label: 'Left' },
  { icon: 'pi pi-align-center', value: 'center', label: 'Centre' },
  { icon: 'pi pi-align-right', value: 'right', label: 'Right' },
]
const heads = [
  { label: 'None', value: 'none' },
  { label: 'Arrow', value: 'arrow' },
  { label: 'Triangle', value: 'triangle' },
  { label: 'Dot', value: 'dot' },
]
const lineTypes = [
  { label: 'Straight', value: 'straight' },
  { label: 'Elbow', value: 'elbow' },
]
const opacities = [1, 0.85, 0.7, 0.5, 0.3].map((v) => ({ label: `${Math.round(v * 100)} %`, value: v }))
</script>

<template>
  <div class="style-editor form">
    <div v-if="keys.has('fill')" class="field">
      <label>{{ elements.every((e) => e.kind === 'text') ? 'Background' : 'Fill' }}</label>
      <ColorField
        :model-value="value('fill')"
        allow-none
        label="Fill"
        :disabled="disabled"
        @update:model-value="(v) => set('fill', v)"
      />
    </div>
    <div v-if="keys.has('stroke')" class="field">
      <label>{{ elements.every((e) => e.kind === 'line') ? 'Colour' : 'Border' }}</label>
      <ColorField
        :model-value="value('stroke')"
        allow-none
        label="Stroke"
        :disabled="disabled"
        @update:model-value="(v) => set('stroke', v)"
      />
    </div>
    <div v-if="keys.has('strokeWidth') || keys.has('strokeStyle')" class="row">
      <div v-if="keys.has('strokeWidth')" class="field">
        <label for="style-width">Width</label>
        <InputNumber
          :model-value="value('strokeWidth') ?? null"
          input-id="style-width"
          size="small"
          :min="0"
          :max="20"
          :max-fraction-digits="1"
          show-buttons
          :disabled="disabled"
          input-class="number"
          @update:model-value="(v: number | null) => set('strokeWidth', v)"
        />
      </div>
      <div v-if="keys.has('strokeStyle')" class="field">
        <label>Style</label>
        <SelectButton
          :model-value="value('strokeStyle') ?? null"
          :options="dashes"
          option-label="label"
          option-value="value"
          size="small"
          :allow-empty="false"
          :disabled="disabled"
          aria-label="Stroke style"
          @update:model-value="(v: string) => set('strokeStyle', v)"
        />
      </div>
    </div>
    <div v-if="keys.has('arrowStart')" class="row">
      <div class="field">
        <label id="style-start-label" for="style-start">Start</label>
        <Select
          :model-value="value('arrowStart') ?? null"
          :options="heads"
          option-label="label"
          option-value="value"
          size="small"
          input-id="style-start"
          aria-labelledby="style-start-label"
          :disabled="disabled"
          @update:model-value="(v: string) => set('arrowStart', v)"
        />
      </div>
      <div class="field">
        <label id="style-end-label" for="style-end">End</label>
        <Select
          :model-value="value('arrowEnd') ?? null"
          :options="heads"
          option-label="label"
          option-value="value"
          size="small"
          input-id="style-end"
          aria-labelledby="style-end-label"
          :disabled="disabled"
          @update:model-value="(v: string) => set('arrowEnd', v)"
        />
      </div>
    </div>
    <div v-if="keys.has('lineType')" class="field">
      <label>Line</label>
      <SelectButton
        :model-value="value('lineType') ?? null"
        :options="lineTypes"
        option-label="label"
        option-value="value"
        size="small"
        :allow-empty="false"
        :disabled="disabled"
        aria-label="Line type"
        @update:model-value="(v: string) => set('lineType', v)"
      />
    </div>
    <div v-if="keys.has('fontSize') || keys.has('bold')" class="row">
      <div v-if="keys.has('fontSize')" class="field">
        <label for="style-font">Font size</label>
        <InputNumber
          :model-value="value('fontSize') ?? null"
          input-id="style-font"
          size="small"
          :min="8"
          :max="120"
          show-buttons
          :disabled="disabled"
          input-class="number"
          @update:model-value="(v: number | null) => set('fontSize', v)"
        />
      </div>
      <div v-if="keys.has('bold')" class="field">
        <label>Text</label>
        <div class="toggles">
          <Button
            label="B"
            size="small"
            :text="!value('bold')"
            severity="secondary"
            aria-label="Bold"
            :aria-pressed="!!value('bold')"
            :disabled="disabled"
            class="bold"
            @click="set('bold', !value('bold'))"
          />
          <Button
            label="I"
            size="small"
            :text="!value('italic')"
            severity="secondary"
            aria-label="Italic"
            :aria-pressed="!!value('italic')"
            :disabled="disabled"
            class="italic"
            @click="set('italic', !value('italic'))"
          />
        </div>
      </div>
    </div>
    <div v-if="keys.has('textColor')" class="field">
      <label>Text colour</label>
      <ColorField
        :model-value="value('textColor')"
        label="Text colour"
        :disabled="disabled"
        @update:model-value="(v) => set('textColor', v)"
      />
    </div>
    <div v-if="keys.has('align')" class="field">
      <label>Alignment</label>
      <SelectButton
        :model-value="value('align') ?? null"
        :options="aligns"
        option-label="label"
        option-value="value"
        data-key="value"
        size="small"
        :allow-empty="false"
        :disabled="disabled"
        aria-label="Alignment"
        @update:model-value="(v: string) => set('align', v)"
      >
        <template #option="{ option }"><i role="img" :class="option.icon" :aria-label="option.label" /></template>
      </SelectButton>
    </div>
    <div v-if="keys.has('radius') || keys.has('opacity')" class="row">
      <div v-if="keys.has('radius')" class="field">
        <label for="style-radius">Corner radius</label>
        <InputNumber
          :model-value="value('radius') ?? null"
          input-id="style-radius"
          size="small"
          :min="0"
          :max="200"
          show-buttons
          :disabled="disabled"
          input-class="number"
          @update:model-value="(v: number | null) => set('radius', v)"
        />
      </div>
      <div v-if="keys.has('opacity')" class="field">
        <label id="style-opacity-label" for="style-opacity">Opacity</label>
        <Select
          :model-value="value('opacity') ?? null"
          :options="opacities"
          option-label="label"
          option-value="value"
          size="small"
          input-id="style-opacity"
          aria-labelledby="style-opacity-label"
          :disabled="disabled"
          @update:model-value="(v: number) => set('opacity', v)"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.style-editor {
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

.row {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pgpm-space-3);
}

.style-editor :deep(.number) {
  width: 4.5rem;
}

.toggles {
  display: flex;
  gap: 2px;
}

.bold :deep(.p-button-label) {
  font-weight: 800;
}

.italic :deep(.p-button-label) {
  font-style: italic;
  font-family: serif;
}
</style>
