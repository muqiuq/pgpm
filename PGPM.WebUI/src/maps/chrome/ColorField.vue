<script setup lang="ts">
import { computed } from 'vue'
import { cssColor, NOTE_COLORS } from '../cards'

/**
 * A colour, saved at once (PNMTD OverviewPanel's picker): the palette's note colours and a few strong ones as
 * swatches, *none* where allowed, and any `#rrggbb` through the browser's colour input.
 */
const props = withDefaults(
  defineProps<{ modelValue: string | null | undefined; allowNone?: boolean; label: string; disabled?: boolean }>(),
  { allowNone: false, disabled: false },
)
const emit = defineEmits<{ 'update:modelValue': [value: string | null] }>()

const STRONG = ['#0f172a', '#475569', '#2563eb', '#16a34a', '#dc2626', '#f59e0b', '#7c3aed', '#ffffff']
const swatches = computed(() => [...NOTE_COLORS, ...STRONG])
const custom = computed(() =>
  props.modelValue && /^#[0-9a-f]{6}$/i.test(props.modelValue) ? props.modelValue : '#000000',
)

function pick(value: string | null) {
  if (!props.disabled && value !== (props.modelValue ?? null)) emit('update:modelValue', value)
}
</script>

<template>
  <div class="color-field" role="radiogroup" :aria-label="label">
    <button
      v-if="allowNone"
      type="button"
      class="swatch none"
      role="radio"
      :aria-checked="modelValue === 'none'"
      aria-label="None"
      title="None"
      :disabled="disabled"
      @click="pick('none')"
    />
    <button
      v-for="s in swatches"
      :key="s"
      type="button"
      class="swatch"
      role="radio"
      :aria-checked="modelValue === s"
      :aria-label="s"
      :title="s"
      :style="{ background: cssColor(s) ?? undefined }"
      :disabled="disabled"
      @click="pick(s)"
    />
    <label class="custom" title="Custom colour">
      <input
        type="color"
        :value="custom"
        :disabled="disabled"
        :aria-label="`${label}: custom`"
        @change="pick(($event.target as HTMLInputElement).value)"
      />
    </label>
    <button
      v-if="modelValue"
      type="button"
      class="reset"
      title="Default"
      aria-label="Default"
      :disabled="disabled"
      @click="pick(null)"
    >
      <i class="pi pi-replay" aria-hidden="true" />
    </button>
  </div>
</template>

<style scoped>
.color-field {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
}

.swatch {
  width: 1.4rem;
  height: 1.4rem;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
  cursor: pointer;
  padding: 0;
}

.swatch[aria-checked='true'] {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.swatch.none {
  background: linear-gradient(
    to top right,
    transparent calc(50% - 1px),
    var(--pgpm-fail) 50%,
    transparent calc(50% + 1px)
  );
}

.custom input {
  width: 1.6rem;
  height: 1.6rem;
  padding: 0;
  border: 0;
  background: none;
  cursor: pointer;
}

.reset {
  border: 0;
  background: none;
  color: var(--pgpm-muted);
  cursor: pointer;
}
</style>
