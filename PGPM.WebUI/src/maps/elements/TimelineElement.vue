<script setup lang="ts">
import { computed } from 'vue'
import { formatDay, milestoneTone, TONE_COLORS } from '../cards'
import type { Detail } from '../culling'
import type { Rect } from '../geometry'
import { timelineLayout } from '../scene'
import type { ElementRow, ItemRow, MilestoneRow } from '../types'

/**
 * A project's milestone timeline (doc 01 §12.3): a line with dots for milestones and diamonds for gates, horizontal or
 * vertical, evenly spaced or by date, coloured as the markers; it follows the project's milestones (all of them, or the
 * subset in `config.milestoneIds`). Plain SVG.
 */
const props = defineProps<{
  element: ElementRow
  frame: Rect
  project: ItemRow | null
  milestones: readonly MilestoneRow[]
  detail: Detail
}>()

const lod = computed(() => props.detail !== 'full')
const shown = computed(() => {
  const subset = props.element.config?.milestoneIds
  return subset?.length ? props.milestones.filter((m) => subset.includes(m.id)) : props.milestones
})
const layout = computed(() => timelineLayout(shown.value, props.frame, props.element.config))
const vertical = computed(() => props.element.config?.orientation === 'vertical')
const showDates = computed(() => props.element.config?.showDates !== false)
const showLabels = computed(() => props.element.config?.showLabels !== false)
const byId = computed(() => new Map(shown.value.map((m) => [m.id, m])))
const tone = (id: string) => {
  const m = byId.value.get(id)
  return m ? TONE_COLORS[milestoneTone(m)] : 'var(--pgpm-none)'
}
const R = 7
const diamond = (x: number, y: number) => `M ${x} ${y - R} L ${x + R} ${y} L ${x} ${y + R} L ${x - R} ${y} Z`
/** Labels alternate above and below a horizontal line; right of a vertical one. */
const labelAt = (i: number, x: number, y: number) =>
  vertical.value ? { x: x + 14, y: y + 4, anchor: 'start' } : { x, y: i % 2 ? y + 24 : y - 14, anchor: 'middle' }
</script>

<template>
  <g
    class="map-element timeline"
    :data-element-id="element.id"
    :transform="`translate(${frame.x} ${frame.y})`"
    role="button"
    :aria-label="`Timeline ${project?.title ?? ''}`"
  >
    <rect class="hit" :width="frame.width" :height="frame.height" rx="6" />
    <text v-if="!lod && project" class="project" x="4" y="12">{{ project.title }}</text>
    <line class="axis" :x1="layout.axis.x1" :y1="layout.axis.y1" :x2="layout.axis.x2" :y2="layout.axis.y2" />
    <g v-for="(m, i) in layout.marks" :key="m.id" :style="{ '--tone': tone(m.id) }">
      <path v-if="m.kind === 'gate'" class="mark" :d="diamond(m.x, m.y)" />
      <circle v-else class="mark" :cx="m.x" :cy="m.y" :r="R - 1" />
      <text
        v-if="!lod && (showLabels || showDates)"
        class="label"
        :x="labelAt(i, m.x, m.y).x"
        :y="labelAt(i, m.x, m.y).y"
        :text-anchor="labelAt(i, m.x, m.y).anchor"
      >
        <tspan v-if="showLabels">{{ m.title }}</tspan>
        <tspan v-if="showDates && m.date" class="date">{{ showLabels ? ' · ' : '' }}{{ formatDay(m.date) }}</tspan>
      </text>
    </g>
    <text
      v-if="!layout.marks.length && !lod"
      class="empty"
      :x="frame.width / 2"
      :y="frame.height / 2 + 18"
      text-anchor="middle"
    >
      No milestones
    </text>
  </g>
</template>

<style scoped>
.timeline {
  cursor: pointer;
}

.hit {
  fill: transparent;
}

.axis {
  stroke: var(--pgpm-muted);
  stroke-width: 2;
}

.mark {
  fill: var(--tone);
  stroke: var(--pgpm-panel);
  stroke-width: 2;
}

.label,
.project,
.empty {
  font-size: 11px;
  fill: var(--pgpm-text);
  pointer-events: none;
}

.project {
  font-weight: 600;
  fill: var(--pgpm-muted);
}

.date,
.empty {
  fill: var(--pgpm-muted);
}
</style>
