<script setup lang="ts">
import { computed } from 'vue'
import { scoreText } from '@/domain/fields/renderValue'

/**
 * A computed score (docs/01 §6.1, docs/03 §16): "58 / 65" – computed on the device, live, never typed in. While an
 * input is missing it shows *incomplete* ("– / 65"). Optional colour thresholds as shares of the maximum (`[{ at: 0.66,
 * color: '#16a34a' }, …]`, the highest one reached wins).
 */
const props = withDefaults(
  defineProps<{
    value: number | null
    max?: number | null
    decimals?: number
    label?: string
    thresholds?: readonly { at: number; color: string }[]
    compact?: boolean
  }>(),
  { max: null, decimals: 0, label: 'Score', thresholds: () => [], compact: false },
)

const text = computed(() => scoreText(props.value, props.max, props.decimals))
const color = computed(() => {
  if (props.value === null || !props.max) return null
  const share = props.value / props.max
  const reached = [...props.thresholds].sort((a, b) => b.at - a.at).find((t) => share >= t.at)
  return reached?.color ?? null
})
const aria = computed(() =>
  props.value === null ? `${props.label}: incomplete` : `${props.label}: ${text.value.replace(' / ', ' of ')}`,
)
</script>

<template>
  <span
    role="img"
    class="score-badge"
    :class="{ incomplete: value === null, compact }"
    :style="color ? { '--score': color } : undefined"
    :aria-label="aria"
    :title="value === null ? 'Incomplete: a value it needs is missing' : undefined"
  >
    <i v-if="!compact" class="pi pi-calculator" aria-hidden="true" />
    <span class="value">{{ text }}</span>
    <small v-if="value === null && !compact" class="muted">incomplete</small>
  </span>
</template>

<style scoped>
.score-badge {
  --score: var(--p-primary-color);
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  padding: 0.0625rem 0.5rem;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--score) 45%, transparent);
  background: color-mix(in srgb, var(--score) 12%, transparent);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  line-height: 1.5;
}

.score-badge i {
  font-size: 0.75rem;
  color: var(--pgpm-muted);
}

.value {
  font-weight: 600;
}

.score-badge.incomplete {
  --score: var(--pgpm-muted);
}

.score-badge.incomplete .value {
  font-weight: 400;
  color: var(--pgpm-muted);
}

.score-badge.compact {
  border: none;
  background: none;
  padding: 0;
}
</style>
