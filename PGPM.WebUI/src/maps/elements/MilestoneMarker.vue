<script setup lang="ts">
import { computed } from 'vue'
import { formatDay, milestoneOverdue, milestoneTone, TONE_COLORS } from '../cards'
import type { Detail } from '../culling'
import type { Rect } from '../geometry'
import type { ElementRow, MilestoneRow } from '../types'

/**
 * A milestone (dot) or gate (diamond) of a project (doc 01 §12.3) with title and due date, coloured by status, a gate
 * by its decision; *overdue* past its due date. Plain SVG. Without the milestone (not readable, or gone): a grey dot.
 */
const props = withDefaults(
  defineProps<{ element: ElementRow; frame: Rect; milestone: MilestoneRow | null; detail: Detail; now: number }>(),
  {},
)

const size = computed(() => Math.max(10, Math.min(props.frame.height, 32)))
const tone = computed(() => (props.milestone ? TONE_COLORS[milestoneTone(props.milestone)] : 'var(--pgpm-none)'))
const done = computed(
  () => props.milestone?.status === 'done' || (props.milestone?.decision ?? 'pending') !== 'pending',
)
const overdue = computed(() => !!props.milestone && milestoneOverdue(props.milestone, props.now))
const diamond = computed(() => {
  const s = size.value / 2
  const cy = props.frame.height / 2
  return `M ${s} ${cy - s} L ${2 * s} ${cy} L ${s} ${cy + s} L 0 ${cy} Z`
})
const title = computed(() => props.milestone?.title ?? 'Restricted')
const date = computed(() => (props.milestone?.due_date ? formatDay(props.milestone.due_date) : null))
</script>

<template>
  <g
    class="map-element milestone"
    :data-element-id="element.id"
    :transform="`translate(${frame.x} ${frame.y})`"
    role="button"
    :aria-label="`${milestone?.kind === 'gate' ? 'Gate' : 'Milestone'} ${title}${overdue ? ' (overdue)' : ''}`"
  >
    <rect class="hit" :width="frame.width" :height="frame.height" />
    <path
      v-if="milestone?.kind === 'gate'"
      class="marker"
      :class="{ filled: done }"
      :d="diamond"
      :style="{ '--tone': tone }"
    />
    <circle
      v-else
      class="marker"
      :class="{ filled: done }"
      :cx="size / 2"
      :cy="frame.height / 2"
      :r="size / 2 - 1"
      :style="{ '--tone': tone }"
    />
    <circle v-if="overdue" class="overdue-ring" :cx="size / 2" :cy="frame.height / 2" :r="size / 2 + 3" />
    <template v-if="detail === 'full'">
      <text class="title" :x="size + 8" :y="frame.height / 2 - (date ? 3 : -4)">{{ title }}</text>
      <text v-if="date" class="date" :class="{ late: overdue }" :x="size + 8" :y="frame.height / 2 + 12">
        {{ date }}{{ overdue ? ' · overdue' : '' }}
      </text>
    </template>
  </g>
</template>

<style scoped>
.milestone {
  cursor: pointer;
}

.hit {
  fill: transparent;
}

.marker {
  fill: var(--pgpm-panel);
  stroke: var(--tone);
  stroke-width: 3;
}

.marker.filled {
  fill: var(--tone);
}

.overdue-ring {
  fill: none;
  stroke: var(--pgpm-fail);
  stroke-width: 2;
  stroke-dasharray: 3 2;
}

.title {
  fill: var(--pgpm-text);
  font-size: 13px;
  font-weight: 600;
  pointer-events: none;
}

.date {
  fill: var(--pgpm-muted);
  font-size: 11px;
  pointer-events: none;
}

.date.late {
  fill: var(--pgpm-fail);
}
</style>
