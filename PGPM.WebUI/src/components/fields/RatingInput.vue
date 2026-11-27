<script setup lang="ts">
import { computed } from 'vue'
import { ratingScale } from '@/domain/fields/fieldWidget'
import type { FieldConfig } from '@/domain/score'

/**
 * A rating (docs/01 §6.1, docs/04 F2): one button per value of the scale (0–5 by default) and *n/a* where allowed –
 * one click sets it, a click on the chosen one clears it. The configured labels (`0`: "does not apply", `5`: "fully
 * applies") are the buttons' tooltips and shown at the ends. v-model: the number, `"na"` or null.
 */
const props = withDefaults(
  defineProps<{ config?: FieldConfig | null; label: string; disabled?: boolean; busy?: boolean }>(),
  { config: null, disabled: false, busy: false },
)
const model = defineModel<number | 'na' | null>({ default: null })

const scale = computed(() => ratingScale(props.config))
const values = computed(() => {
  const list: number[] = []
  for (let v = scale.value.min; v <= scale.value.max; v++) list.push(v)
  return list
})

function pick(value: number | 'na') {
  if (props.disabled || props.busy) return
  model.value = model.value === value ? null : value
}

const title = (v: number) => scale.value.labels[String(v)] ?? String(v)
/** "Reach: nobody, 0": the visible digit is part of the name (WCAG 2.5.3 label in name; axe ignores parentheses). */
const name = (v: number) => (title(v) === String(v) ? `${props.label}: ${v}` : `${props.label}: ${title(v)}, ${v}`)
</script>

<template>
  <div class="rating" role="group" :aria-label="label" :class="{ disabled }">
    <button
      v-for="v in values"
      :key="v"
      type="button"
      class="value"
      :class="{ on: model === v, below: typeof model === 'number' && v < model }"
      :aria-pressed="model === v"
      :aria-label="name(v)"
      :title="title(v)"
      :disabled="disabled"
      @click="pick(v)"
    >
      {{ v }}
    </button>
    <button
      v-if="scale.allowNa"
      type="button"
      class="value na"
      :class="{ on: model === 'na' }"
      :aria-pressed="model === 'na'"
      :aria-label="`${label}: not applicable, n/a`"
      title="Not applicable"
      :disabled="disabled"
      @click="pick('na')"
    >
      n/a
    </button>
  </div>
</template>

<style scoped>
.rating {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 2px;
}

.value {
  all: unset;
  box-sizing: border-box;
  min-width: 26px;
  height: 26px;
  padding: 0 0.375rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-sm);
  font-size: 0.857rem;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  color: var(--pgpm-text);
  background: var(--pgpm-panel);
}

.value:not(:disabled):hover,
.value:focus-visible {
  border-color: var(--p-primary-color);
}

.value:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.value.below {
  background: color-mix(in srgb, var(--p-primary-color) 14%, transparent);
}

.value.on {
  background: var(--p-primary-color);
  border-color: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-weight: 600;
}

.value.na.on {
  background: var(--pgpm-muted);
  border-color: var(--pgpm-muted);
}

.value:disabled {
  cursor: default;
  opacity: 0.85;
}

.rating.disabled .value:not(.on):not(.below) {
  color: var(--pgpm-muted);
}
</style>
