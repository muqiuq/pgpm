<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import {
  formatDay,
  itemKey,
  milestoneTone,
  MILESTONE_STATUS_LABELS,
  TONE_COLORS,
  TYPE_LABELS,
  type CardModel,
} from '../cards'
import type { ItemRow, LinkRelation, LinkRow, MilestoneRow } from '../types'

/**
 * An item card's summary in the side panel (doc 01 §12.5), below the host's quick edit (`#item-panel`): status,
 * persons, milestones, scores, description, links, and how often the item is on this map.
 */
const props = defineProps<{
  item: ItemRow
  card: CardModel
  cardCount: number
  milestones: readonly MilestoneRow[]
  links: readonly LinkRow[]
  itemsById: ReadonlyMap<string, ItemRow>
  quadrant: string | null
}>()
const emit = defineEmits<{ selectAll: [] }>()

const RELATIONS: Record<LinkRelation, [string, string]> = {
  contributes_to: ['contributes to', 'contributed to by'],
  learning_of: ['is learning of', 'has learning'],
  depends_on: ['depends on', 'is needed by'],
  related: ['related to', 'related to'],
}

const linkRows = computed(() =>
  props.links
    .filter((l) => l.from_item_id === props.item.id || l.to_item_id === props.item.id)
    .map((l) => {
      const outgoing = l.from_item_id === props.item.id
      const other = props.itemsById.get(outgoing ? l.to_item_id : l.from_item_id)
      return {
        id: l.id,
        relation: RELATIONS[l.relation]?.[outgoing ? 0 : 1] ?? l.relation,
        label: other ? `${itemKey(other)} ${other.title}` : 'Restricted',
        restricted: !other,
      }
    }),
)
</script>

<template>
  <section>
    <h3>Details</h3>
    <dl class="facts">
      <dt>Type</dt>
      <dd>{{ TYPE_LABELS[item.type] }}</dd>
      <template v-if="card.status">
        <dt>Status</dt>
        <dd>
          {{ card.status.name }}
          <span v-if="card.overdue" class="overdue">overdue</span>
        </dd>
      </template>
      <template v-else-if="card.overdue">
        <dt>Status</dt>
        <dd><span class="overdue">overdue</span></dd>
      </template>
      <template v-if="quadrant">
        <dt>Quadrant</dt>
        <dd>{{ quadrant }}</dd>
      </template>
      <dt>Persons</dt>
      <dd :class="{ muted: !card.persons }">{{ card.persons || 'None assigned' }}</dd>
      <template v-for="b in card.badges" :key="b.key">
        <dt>{{ b.label }}</dt>
        <dd>{{ b.text }}</dd>
      </template>
      <template v-if="card.progress">
        <dt>Milestones</dt>
        <dd>{{ card.progress.done }} of {{ card.progress.total }} done</dd>
      </template>
    </dl>
  </section>
  <section>
    <h3>Description</h3>
    <MarkdownView v-if="item.description" :source="item.description" />
    <p v-else class="muted small">No description.</p>
  </section>
  <section v-if="milestones.length">
    <h3>
      Milestones <span class="count">{{ milestones.length }}</span>
    </h3>
    <ul class="rows">
      <li v-for="m in milestones" :key="m.id">
        <span
          class="dot"
          :class="{ gate: m.kind === 'gate' }"
          :style="{ background: TONE_COLORS[milestoneTone(m)] }"
          aria-hidden="true"
        />
        <span class="row-name">{{ m.title }}</span>
        <span class="muted small">{{ formatDay(m.due_date) }} · {{ MILESTONE_STATUS_LABELS[m.status] }}</span>
      </li>
    </ul>
  </section>
  <section v-if="linkRows.length">
    <h3>
      Links <span class="count">{{ linkRows.length }}</span>
    </h3>
    <ul class="rows">
      <li v-for="l in linkRows" :key="l.id">
        <span class="muted small">{{ l.relation }}</span>
        <span class="row-name" :class="{ muted: l.restricted }">{{ l.label }}</span>
      </li>
    </ul>
  </section>
  <section v-if="cardCount > 1">
    <p class="appears">
      This item appears {{ cardCount }}× on this map.
      <Button label="Select all" size="small" text @click="emit('selectAll')" />
    </p>
  </section>
</template>

<style scoped>
.overdue {
  margin-left: 0.4rem;
  padding: 0 6px;
  border-radius: 999px;
  color: var(--pgpm-fail);
  background: var(--pgpm-fail-soft);
  font-size: 0.857rem;
  font-weight: 600;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.rows li {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--pgpm-space-2);
}

.row-name {
  font-weight: 500;
}

.dot {
  width: 0.65rem;
  height: 0.65rem;
  border-radius: 50%;
  flex: none;
}

.dot.gate {
  border-radius: 1px;
  transform: rotate(45deg) scale(0.9);
}

.small {
  font-size: 0.857rem;
}

.appears {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  margin: 0;
}
</style>
