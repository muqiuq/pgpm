<script setup lang="ts">
import { computed } from 'vue'
import { DEFAULT_QUADRANT_LABELS, splitOf, type MatrixLayout, type Quadrant } from './matrixLayout'
import type { MatrixSettings } from './types'

/**
 * A matrix map's fixed background (docs/03 §13.5): the plot, the four quadrants with their labels, the split lines and
 * the axes with their labels and ranges. Plain SVG under the cards.
 */
const props = defineProps<{ layout: MatrixLayout; config: MatrixSettings }>()

const QUADRANTS: readonly Quadrant[] = ['tl', 'tr', 'bl', 'br']
const label = (q: Quadrant) => props.config.quadrants?.[q] || DEFAULT_QUADRANT_LABELS[q]
const plot = computed(() => props.layout.plot)
const labelAt = (q: Quadrant) => {
  const b = props.layout.quadrants[q]
  const left = q.endsWith('l')
  return {
    x: left ? b.minX + 16 : b.maxX - 16,
    y: q.startsWith('t') ? b.minY + 32 : b.maxY - 18,
    anchor: left ? 'start' : 'end',
  }
}
const fmt = (v: number) => (Number.isInteger(v) ? String(v) : v.toFixed(1))
</script>

<template>
  <g class="matrix-background" aria-hidden="true">
    <rect
      class="world"
      :x="layout.world.minX"
      :y="layout.world.minY"
      :width="layout.world.maxX - layout.world.minX"
      :height="layout.world.maxY - layout.world.minY"
    />
    <rect
      v-for="q in QUADRANTS"
      :key="q"
      class="quadrant"
      :class="`q-${q}`"
      :x="layout.quadrants[q].minX"
      :y="layout.quadrants[q].minY"
      :width="Math.max(0, layout.quadrants[q].maxX - layout.quadrants[q].minX)"
      :height="Math.max(0, layout.quadrants[q].maxY - layout.quadrants[q].minY)"
    />
    <text
      v-for="q in QUADRANTS"
      :key="`label-${q}`"
      class="quadrant-label"
      :x="labelAt(q).x"
      :y="labelAt(q).y"
      :text-anchor="labelAt(q).anchor"
    >
      {{ label(q) }}
    </text>
    <line class="split" :x1="layout.split.x" :y1="plot.minY" :x2="layout.split.x" :y2="plot.maxY" />
    <line class="split" :x1="plot.minX" :y1="layout.split.y" :x2="plot.maxX" :y2="layout.split.y" />
    <line class="axis" :x1="plot.minX" :y1="plot.maxY" :x2="plot.maxX" :y2="plot.maxY" />
    <line class="axis" :x1="plot.minX" :y1="plot.maxY" :x2="plot.minX" :y2="plot.minY" />
    <text class="axis-label" :x="(plot.minX + plot.maxX) / 2" :y="plot.maxY + 56" text-anchor="middle">
      {{ config.x.label || config.x.field }} →
    </text>
    <text
      class="axis-label"
      :transform="`translate(${plot.minX - 56} ${(plot.minY + plot.maxY) / 2}) rotate(-90)`"
      text-anchor="middle"
    >
      {{ config.y.label || config.y.field }} →
    </text>
    <text class="tick" :x="plot.minX" :y="plot.maxY + 24" text-anchor="middle">{{ fmt(config.x.min) }}</text>
    <text class="tick" :x="layout.split.x" :y="plot.maxY + 24" text-anchor="middle">{{ fmt(splitOf(config.x)) }}</text>
    <text class="tick" :x="plot.maxX" :y="plot.maxY + 24" text-anchor="middle">{{ fmt(config.x.max) }}</text>
    <text class="tick" :x="plot.minX - 12" :y="plot.maxY + 4" text-anchor="end">{{ fmt(config.y.min) }}</text>
    <text class="tick" :x="plot.minX - 12" :y="layout.split.y + 4" text-anchor="end">{{ fmt(splitOf(config.y)) }}</text>
    <text class="tick" :x="plot.minX - 12" :y="plot.minY + 4" text-anchor="end">{{ fmt(config.y.max) }}</text>
  </g>
</template>

<style scoped>
.matrix-background {
  pointer-events: none;
}

.world {
  fill: none;
}

.quadrant {
  stroke: none;
}

.q-tr {
  fill: color-mix(in srgb, var(--pgpm-ok) 7%, transparent);
}

.q-br {
  fill: color-mix(in srgb, var(--p-primary-color) 6%, transparent);
}

.q-tl {
  fill: color-mix(in srgb, var(--pgpm-warn) 7%, transparent);
}

.q-bl {
  fill: color-mix(in srgb, var(--pgpm-none) 8%, transparent);
}

.quadrant-label {
  fill: var(--pgpm-muted);
  font-size: 24px;
  font-weight: 600;
  opacity: 0.75;
}

.split {
  stroke: var(--pgpm-muted);
  stroke-width: 1.5;
  stroke-dasharray: 8 6;
}

.axis {
  stroke: var(--pgpm-text);
  stroke-width: 2;
}

.axis-label {
  fill: var(--pgpm-text);
  font-size: 20px;
  font-weight: 600;
}

.tick {
  fill: var(--pgpm-muted);
  font-size: 14px;
}
</style>
