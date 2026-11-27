<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { presetColors } from '@/domain/itemTypes'

/**
 * An item's colour (docs/01 §4.1): the 12 presets, a custom `#rrggbb`, or none (the type's default). v-model is the
 * colour or null; the choice is emitted at once (`change`), so it works inline and in dialogs.
 */
const props = withDefaults(defineProps<{ disabled?: boolean; idPrefix?: string }>(), {
  disabled: false,
  idPrefix: 'color',
})
const color = defineModel<string | null>({ default: null })
const emit = defineEmits<{ change: [value: string | null] }>()

const custom = ref(color.value ?? '')
watch(color, (value) => (custom.value = value ?? ''))
const valid = computed(() => /^#[0-9a-f]{6}$/i.test(custom.value.trim()))

function pick(value: string | null) {
  if (props.disabled) return
  color.value = value
  emit('change', value)
}

function applyCustom() {
  if (valid.value) pick(custom.value.trim().toLowerCase())
}
</script>

<template>
  <div class="color-picker" role="group" aria-label="Colour">
    <div class="swatches">
      <button
        v-for="preset in presetColors"
        :key="preset.value"
        type="button"
        class="swatch"
        :class="{ selected: color?.toLowerCase() === preset.value }"
        :style="{ background: preset.value }"
        :aria-label="preset.name"
        :aria-pressed="color?.toLowerCase() === preset.value"
        :title="preset.name"
        :disabled="disabled"
        @click="pick(preset.value)"
      />
      <button
        type="button"
        class="swatch none"
        :class="{ selected: !color }"
        aria-label="No colour"
        :aria-pressed="!color"
        title="No colour"
        :disabled="disabled"
        @click="pick(null)"
      >
        <i class="pi pi-ban" aria-hidden="true" />
      </button>
    </div>
    <div class="custom">
      <span class="preview" :style="{ background: valid ? custom : 'transparent' }" aria-hidden="true" />
      <InputText
        :id="`${idPrefix}-hex`"
        v-model="custom"
        size="small"
        placeholder="#rrggbb"
        maxlength="7"
        aria-label="Custom colour (#rrggbb)"
        :invalid="!!custom && !valid"
        :disabled="disabled"
        @keydown.enter.prevent="applyCustom"
      />
      <Button
        label="Use"
        size="small"
        severity="secondary"
        outlined
        :disabled="disabled || !valid"
        @click="applyCustom"
      />
    </div>
  </div>
</template>

<style scoped>
.color-picker {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.swatches {
  display: grid;
  grid-template-columns: repeat(7, 1.75rem);
  gap: var(--pgpm-space-1);
}

.swatch {
  width: 1.75rem;
  height: 1.75rem;
  border-radius: var(--p-border-radius-sm);
  border: 2px solid transparent;
  cursor: pointer;
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.swatch.selected {
  border-color: var(--pgpm-text);
  box-shadow: 0 0 0 2px var(--pgpm-panel) inset;
}

.swatch.none {
  background: var(--pgpm-panel);
  border-color: var(--pgpm-border);
  color: var(--pgpm-muted);
}

.swatch.none.selected {
  border-color: var(--pgpm-text);
}

.swatch:disabled {
  cursor: default;
  opacity: 0.6;
}

.custom {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.custom :deep(input) {
  width: 7rem;
}

.preview {
  width: 1.25rem;
  height: 1.25rem;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
}
</style>
