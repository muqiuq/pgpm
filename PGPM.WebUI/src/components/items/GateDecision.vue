<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { decisionLabels, decisionOptions, isDecided, suggestedStatus } from '@/domain/milestones'
import { localDb } from '@/local/db'
import { useStatuses, useUserPersons } from '@/local/lookups'
import { applySuggestedStatus, decideGate } from '@/local/milestones'
import { repo as appRepo, type Repo } from '@/local/repo'
import type { GateDecisionValue, ItemRow, MilestoneRow, StatusRow, Stored, TemplateStatusRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import StatusBadge from './StatusBadge.vue'

/**
 * A gate's decision (docs/01 §11, docs/03 §12.7): *pending / go / no-go / iterate / pivot / park* with a note, who
 * decided and when (set by the server). Recording *go* or *no-go* then offers the status the template suggests
 * (`onGoStatusId` / `onNoGoStatusId`, if the project's template allows it and the project isn't in it yet): the user
 * confirms with *Move project to …* or keeps the status. Written to the local data (offline too); read-only without
 * *edit*.
 */
const props = withDefaults(
  defineProps<{ milestone: Stored<MilestoneRow>; project: ItemRow; canEdit: boolean; repo?: Repo }>(),
  { repo: undefined },
)
const notify = useNotify()
const statuses = useStatuses()
const people = useUserPersons()
const writer = computed(() => props.repo ?? appRepo)

/** The statuses the project's template allows (none: every project status). */
const allowed = useLive(
  async () =>
    props.project.projectTemplateId
      ? (
          (await localDb()
            .synced('template_statuses')
            .where('templateId')
            .equals(props.project.projectTemplateId)
            .toArray()) as unknown as TemplateStatusRow[]
        )
          .filter((s) => !s.deleted)
          .map((s) => s.statusId)
      : [],
  [] as string[],
  () => props.project.projectTemplateId,
)

const decision = ref<GateDecisionValue>(props.milestone.decision ?? 'pending')
const note = ref(props.milestone.decisionNote ?? '')
const saving = ref(false)
/** The status offered after the last decision recorded here. */
const offer = ref<StatusRow | null>(null)

watch(
  () => [props.milestone.decision, props.milestone.decisionNote] as const,
  ([d, n]) => {
    if (saving.value) return
    decision.value = d ?? 'pending'
    note.value = n ?? ''
  },
)

const changed = computed(
  () =>
    decision.value !== (props.milestone.decision ?? 'pending') ||
    (note.value.trim() || null) !== (props.milestone.decisionNote ?? null),
)
const decided = computed(() => isDecided(props.milestone.decision))
const decidedBy = computed(() =>
  props.milestone.decidedBy ? people.nameOf(props.milestone.decidedBy, 'Someone') : null,
)
const idBase = computed(() => `gate-${props.milestone.id}`)

async function record() {
  if (!changed.value || saving.value) return
  saving.value = true
  try {
    const chosen = decision.value
    await decideGate(writer.value, props.milestone.id, chosen, note.value)
    offer.value = suggestedStatus(props.milestone, chosen, props.project, statuses.byId.value, allowed.value.value)
    notify.success(`Decision recorded: ${decisionLabels[chosen]}`)
  } catch (e) {
    notify.error(e, 'Decision not recorded')
  } finally {
    saving.value = false
  }
}

async function accept() {
  const status = offer.value
  if (!status) return
  try {
    await applySuggestedStatus(writer.value, props.project.id, status.id)
    offer.value = null
    notify.success(`The project is ${status.name} now`)
  } catch (e) {
    notify.error(e, 'Status not changed')
  }
}
</script>

<template>
  <div class="gate-decision" :data-testid="`gate-decision-${milestone.id}`">
    <div class="choices" role="radiogroup" :aria-labelledby="`${idBase}-label`">
      <span :id="`${idBase}-label`" class="label">Decision</span>
      <Button
        v-for="o in decisionOptions"
        :key="o.value"
        :label="o.label"
        :icon="o.icon"
        size="small"
        :severity="decision === o.value ? o.severity : 'secondary'"
        :outlined="decision !== o.value"
        role="radio"
        :aria-checked="decision === o.value"
        :disabled="!canEdit || saving"
        @click="decision = o.value"
      />
    </div>
    <div v-if="canEdit" class="note">
      <label :for="`${idBase}-note`" class="label">Decision note</label>
      <Textarea
        :id="`${idBase}-note`"
        v-model="note"
        rows="2"
        auto-resize
        maxlength="4000"
        :disabled="saving"
        placeholder="Why, and on what conditions…"
      />
      <span class="actions">
        <Button
          label="Record decision"
          icon="pi pi-check"
          size="small"
          :disabled="!changed"
          :loading="saving"
          @click="record"
        />
        <Button
          v-if="changed"
          label="Discard"
          size="small"
          severity="secondary"
          text
          :disabled="saving"
          @click="
            () => {
              decision = milestone.decision ?? 'pending'
              note = milestone.decisionNote ?? ''
            }
          "
        />
      </span>
    </div>
    <p v-else-if="milestone.decisionNote" class="read-note">{{ milestone.decisionNote }}</p>
    <p v-if="decided" class="muted who">
      Decided
      <template v-if="decidedBy">by {{ decidedBy }}</template>
      <template v-if="milestone.decidedAt"> · <RelativeTime :value="milestone.decidedAt" /></template>
      <template v-else-if="milestone._local?.pending"> · not synced yet</template>
    </p>
    <Message v-if="offer" severity="info" :closable="false" class="offer" data-testid="status-offer">
      <span class="offer-line"> The template suggests moving the project to <StatusBadge :status="offer" /> </span>
      <span class="actions">
        <Button :label="`Move project to ${offer.name}`" size="small" @click="accept" />
        <Button label="Keep the status" size="small" severity="secondary" text @click="offer = null" />
      </span>
    </Message>
  </div>
</template>

<style scoped>
.gate-decision {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.choices {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.label {
  font-weight: 600;
  margin-right: var(--pgpm-space-2);
}

.note {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.actions {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
}

.read-note {
  margin: 0;
  white-space: pre-wrap;
}

.who {
  margin: 0;
  font-size: 0.857rem;
}

.offer-line {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
  margin-right: var(--pgpm-space-3);
}
</style>
