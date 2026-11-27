<script setup lang="ts">
import { computed } from 'vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import type { CardModel } from '../cards'
import type { Detail } from '../culling'
import type { Rect } from '../geometry'
import { CARD_RADIUS } from '../scene'
import type { ElementRow } from '../types'

/**
 * An item card (doc 01 §12.3, docs/03 §13.1): a `<rect class="paper">` and a `<foreignObject>` with the HTML card –
 * type icon, key, status (+ overdue), title, the cover image and description when expanded (a note's text always, as a
 * compact Markdown excerpt), persons, milestone progress, the map's badges and a matrix map's quadrant. Below the
 * level-of-detail zoom: a coloured rectangle with the title in plain SVG. Without a card model the item isn't readable:
 * *restricted*.
 */
const props = withDefaults(
  defineProps<{
    element: ElementRow
    frame: Rect
    card: CardModel | null
    detail: Detail
    selected?: boolean
    imageUrl?: string | null
    /** A matrix map's quadrant label. */
    quadrant?: string | null
    /** Moves are animated (matrix cards that move when values change). */
    animated?: boolean
  }>(),
  { selected: false, imageUrl: null, quadrant: null, animated: false },
)

/** Matrix cards scale with their bubble size: the content is drawn at the standard size and scaled. */
const scale = computed(() => {
  const s = props.element.config?.scale
  return typeof s === 'number' && s > 0 ? s : 1
})
const width = computed(() => props.frame.width / scale.value)
const height = computed(() => props.frame.height / scale.value)
const lod = computed(() => props.detail !== 'full')
const expanded = computed(() => props.element.config?.size === 'expanded')
/** A low card (a matrix map's): one line of title, the badges on one line. */
const dense = computed(() => height.value < 72)
const excerpt = computed(() => {
  const c = props.card
  if (!c?.description) return null
  return c.type === 'note' || expanded.value ? c.description : null
})
const place = computed(() =>
  props.animated
    ? { style: { transform: `translate(${props.frame.x}px, ${props.frame.y}px)` } }
    : { transform: `translate(${props.frame.x} ${props.frame.y})` },
)
const lodTitle = computed(() => {
  const title = props.card?.title ?? 'Restricted'
  const max = Math.max(4, Math.floor((width.value - 20) / 7.2))
  return title.length > max ? `${title.slice(0, max - 1)}…` : title
})
const label = computed(() =>
  props.card ? `${props.card.key} ${props.card.title}${props.card.overdue ? ' (overdue)' : ''}` : 'Restricted item',
)
</script>

<template>
  <g
    class="map-element item-card"
    :class="{ selected, closed: card?.closed, restricted: !card, animated }"
    :data-element-id="element.id"
    role="button"
    :aria-label="label"
    v-bind="place"
  >
    <g :transform="scale === 1 ? undefined : `scale(${scale})`">
      <rect
        class="paper"
        :width="width"
        :height="height"
        :rx="CARD_RADIUS"
        :style="card ? { fill: card.fill } : undefined"
      />
      <rect
        v-if="card && card.fill !== card.color"
        class="accent"
        :width="4"
        :height="height"
        rx="2"
        :style="{ fill: card.color }"
      />
      <template v-if="!card || lod">
        <text v-if="detail !== 'far'" class="lod-title" :x="10" :y="height / 2" dominant-baseline="central">
          {{ lodTitle }}
        </text>
      </template>
      <foreignObject v-else :width="width" :height="height">
        <div xmlns="http://www.w3.org/1999/xhtml" class="card" :class="[`type-${card.type}`, { dense }]">
          <div class="head">
            <i :class="card.icon" aria-hidden="true" />
            <span class="key">{{ card.key }}</span>
            <span v-if="card.status" class="status" :style="{ '--status': card.status.color }">{{
              card.status.name
            }}</span>
            <span v-if="card.overdue" class="overdue">overdue</span>
          </div>
          <img v-if="expanded && imageUrl" class="cover" :src="imageUrl" alt="" draggable="false" />
          <div class="title">{{ card.title }}</div>
          <MarkdownView v-if="excerpt" class="excerpt" :source="excerpt" :compact="expanded ? 3 : 1" />
          <div v-if="card.persons || card.progress || card.badges.length || quadrant" class="badges">
            <span v-if="card.persons" class="badge" :title="`${card.persons} assigned`"
              ><i class="pi pi-users" aria-hidden="true" />{{ card.persons }}</span
            >
            <span v-if="card.progress" class="badge" title="Milestones done"
              ><i class="pi pi-flag-fill" aria-hidden="true" />{{ card.progress.done }}/{{ card.progress.total }}</span
            >
            <span v-for="b in card.badges" :key="b.key" class="badge score" :title="b.label"
              ><span class="badge-label">{{ b.label }}</span> {{ b.text }}</span
            >
            <span v-if="quadrant" class="badge quadrant">{{ quadrant }}</span>
          </div>
        </div>
      </foreignObject>
    </g>
  </g>
</template>

<style scoped>
.item-card {
  cursor: pointer;
}

.item-card.animated {
  transition: transform 0.45s ease;
}

.paper {
  stroke: color-mix(in srgb, var(--pgpm-text) 18%, transparent);
  stroke-width: 1;
}

.accent {
  pointer-events: none;
}

.restricted .paper {
  fill: var(--pgpm-none-soft);
  stroke: var(--pgpm-none);
  stroke-dasharray: 5 4;
}

.closed {
  opacity: 0.55;
}

.lod-title {
  fill: var(--pgpm-text);
  font-size: 14px;
  font-weight: 600;
  pointer-events: none;
}

.card {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  overflow: hidden;
  padding: 6px 9px 6px;
  display: flex;
  flex-direction: column;
  gap: 3px;
  font-size: 12px;
  line-height: 1.35;
  color: var(--pgpm-text);
  user-select: none;
}

.head {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  white-space: nowrap;
  color: var(--pgpm-muted);
  font-size: 11px;
}

.key {
  font-weight: 600;
  font-feature-settings: 'tnum';
}

.status {
  margin-left: auto;
  padding: 0 6px;
  border-radius: 999px;
  border: 1px solid var(--status, var(--pgpm-none));
  color: var(--pgpm-text);
  background: color-mix(in srgb, var(--status, var(--pgpm-none)) 16%, var(--pgpm-panel));
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 55%;
}

.overdue {
  padding: 0 5px;
  border-radius: 999px;
  color: var(--pgpm-fail);
  background: var(--pgpm-fail-soft);
  font-weight: 600;
}

.title {
  font-size: 13px;
  font-weight: 600;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}

.cover {
  width: 100%;
  max-height: 45%;
  object-fit: cover;
  border-radius: 4px;
}

.excerpt {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  font-size: 11.5px;
  color: var(--pgpm-text);
  opacity: 0.85;
  pointer-events: none;
}

.dense {
  padding: 4px 7px;
  gap: 1px;
}

.dense .title {
  -webkit-line-clamp: 1;
  font-size: 12.5px;
}

.dense .badges {
  flex-wrap: nowrap;
  overflow: hidden;
}

.badges {
  margin-top: auto;
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  font-size: 11px;
}

.badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 0 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--pgpm-panel) 70%, transparent);
  white-space: nowrap;
}

.badge .pi {
  font-size: 10px;
}

.badge-label {
  color: var(--pgpm-muted);
}

.quadrant {
  font-weight: 600;
}
</style>
