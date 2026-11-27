<script setup lang="ts">
import { computed } from 'vue'
import { DECISION_LABELS, dodProgress, formatDay, itemKey, milestoneOverdue, MILESTONE_STATUS_LABELS } from '../cards'
import type { DodItemRow, ItemRow, MilestoneRow } from '../types'

/** A milestone or gate in the side panel (doc 01 §11, §12.5): details, the Definition of Done, the gate decision. */
const props = defineProps<{
  milestone: MilestoneRow
  project: ItemRow | null
  dod: readonly DodItemRow[]
  now: number
}>()

const progress = computed(() => dodProgress(props.dod))
const overdue = computed(() => milestoneOverdue(props.milestone, props.now))
const STATE_ICONS: Record<DodItemRow['state'], string> = {
  open: 'pi pi-circle',
  done: 'pi pi-check-circle',
  na: 'pi pi-minus-circle',
}
</script>

<template>
  <section>
    <h3>Details</h3>
    <dl class="facts">
      <dt>Project</dt>
      <dd :class="{ muted: !project }">{{ project ? `${itemKey(project)} ${project.title}` : 'Restricted' }}</dd>
      <dt>Kind</dt>
      <dd>{{ milestone.kind === 'gate' ? 'Gate' : 'Milestone' }}</dd>
      <dt>Due</dt>
      <dd>{{ formatDay(milestone.due_date) }}<span v-if="overdue" class="overdue">overdue</span></dd>
      <dt>Status</dt>
      <dd>{{ MILESTONE_STATUS_LABELS[milestone.status] }}</dd>
      <template v-if="milestone.kind === 'gate'">
        <dt>Decision</dt>
        <dd>{{ DECISION_LABELS[milestone.decision ?? 'pending'] }}</dd>
        <template v-if="milestone.decision_note">
          <dt>Note</dt>
          <dd>{{ milestone.decision_note }}</dd>
        </template>
      </template>
    </dl>
  </section>
  <section>
    <h3>
      Definition of Done <span v-if="dod.length" class="count">{{ progress.done }} / {{ progress.total }}</span>
    </h3>
    <p v-if="!dod.length" class="muted small">No criteria.</p>
    <ul v-else class="dod">
      <li v-for="d in dod" :key="d.id" :class="`state-${d.state}`">
        <i :class="STATE_ICONS[d.state]" aria-hidden="true" />
        <span>{{ d.text }}</span>
        <span v-if="d.note" class="muted small">{{ d.note }}</span>
      </li>
    </ul>
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

.dod {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.dod li {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--pgpm-space-2);
}

.state-done i {
  color: var(--pgpm-ok);
}

.state-na {
  color: var(--pgpm-muted);
}

.small {
  font-size: 0.857rem;
}
</style>
