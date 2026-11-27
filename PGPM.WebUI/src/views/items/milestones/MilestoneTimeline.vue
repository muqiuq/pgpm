<script setup lang="ts">
import { computed } from 'vue'
import { decisionLabels, isDecided, isMilestoneOverdue, kindLabels, statusLabels } from '@/domain/milestones'
import type { MilestoneRow } from '@/local/rows'
import { formatDay } from '@/views/items/itemList'

/**
 * The project's milestones on a mini timeline (docs/01 §11): by due date, gates as diamonds, milestones as dots; done
 * ones filled, overdue ones red, cancelled ones faded; a dashed line for today. Positions are percentages of the width
 * (no viewBox), so the marks keep their shape at any width. Those without a due date are counted below. A click (or
 * Enter) selects the milestone in the list (`select`).
 */
const props = defineProps<{ milestones: readonly MilestoneRow[]; today: string }>()
const emit = defineEmits<{ select: [id: string] }>()

const axis = 24
const dayMs = 24 * 60 * 60 * 1000
const time = (iso: string) => Date.parse(`${iso}T00:00:00Z`)

const dated = computed(() => props.milestones.filter((m) => !!m.dueDate))
const undated = computed(() => props.milestones.length - dated.value.length)

const range = computed(() => {
  const times = [...dated.value.map((m) => time(m.dueDate!)), time(props.today)]
  let from = Math.min(...times)
  let to = Math.max(...times)
  if (to - from < 14 * dayMs) {
    const mid = (from + to) / 2
    from = mid - 7 * dayMs
    to = mid + 7 * dayMs
  }
  return { from, to }
})
/** A date's place, 2–98 % of the width. */
const at = (iso: string) => `${2 + ((time(iso) - range.value.from) / (range.value.to - range.value.from)) * 96}%`

const marks = computed(() =>
  dated.value.map((m) => {
    const overdue = isMilestoneOverdue(m, props.today)
    const parts = [
      `${kindLabels[m.kind]} ${m.title}`,
      `due ${formatDay(m.dueDate)}`,
      statusLabels[m.status],
      ...(m.kind === 'gate' && isDecided(m.decision) ? [`decision ${decisionLabels[m.decision]}`] : []),
      ...(overdue ? ['overdue'] : []),
    ]
    return {
      id: m.id,
      x: at(m.dueDate!),
      gate: m.kind === 'gate',
      done: m.status === 'done',
      cancelled: m.status === 'cancelled',
      overdue,
      color: m.color,
      label: parts.join(' · '),
    }
  }),
)
const todayX = computed(() => at(props.today))
const ends = computed(() => ({
  from: formatDay(new Date(range.value.from).toISOString().slice(0, 10)),
  to: formatDay(new Date(range.value.to).toISOString().slice(0, 10)),
}))
</script>

<template>
  <figure class="timeline" data-testid="milestone-timeline">
    <svg v-if="dated.length" width="100%" height="48" role="group" aria-label="Timeline of the milestones">
      <line class="axis" x1="2%" x2="98%" :y1="axis" :y2="axis" />
      <line class="today" :x1="todayX" :x2="todayX" y1="4" y2="44" />
      <svg
        v-for="m in marks"
        :key="m.id"
        :x="m.x"
        :y="axis"
        overflow="visible"
        :class="['mark', { done: m.done, overdue: m.overdue, cancelled: m.cancelled, gate: m.gate }]"
        :style="m.color ? { '--mark': m.color } : undefined"
        role="button"
        tabindex="0"
        :aria-label="m.label"
        @click="emit('select', m.id)"
        @keydown.enter.prevent="emit('select', m.id)"
      >
        <title>{{ m.label }}</title>
        <rect v-if="m.gate" x="-6" y="-6" width="12" height="12" transform="rotate(45)" />
        <circle v-else r="6" />
      </svg>
    </svg>
    <figcaption class="muted">
      <template v-if="dated.length">
        <span>{{ ends.from }}</span>
        <span>{{ ends.to }}</span>
      </template>
      <span v-if="undated" class="undated">{{
        undated === 1 ? '1 without a due date' : `${undated} without a due date`
      }}</span>
    </figcaption>
  </figure>
</template>

<style scoped>
.timeline {
  margin: 0;
}

svg {
  display: block;
}

.axis {
  stroke: var(--pgpm-border);
  stroke-width: 2;
}

.today {
  stroke: var(--p-primary-color);
  stroke-dasharray: 3 3;
}

.mark {
  --mark: var(--p-primary-color);
  cursor: pointer;
  outline: none;
}

.mark rect,
.mark circle {
  fill: var(--pgpm-panel);
  stroke: var(--mark);
  stroke-width: 2;
}

.mark.done rect,
.mark.done circle {
  fill: var(--mark);
}

.mark.overdue rect,
.mark.overdue circle {
  stroke: var(--pgpm-fail);
}

.mark.cancelled {
  opacity: 0.4;
}

.mark:focus-visible rect,
.mark:focus-visible circle {
  stroke-width: 4;
}

figcaption {
  display: flex;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
  font-size: 0.786rem;
}

.undated {
  margin-left: auto;
}
</style>
