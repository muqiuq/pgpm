<script setup lang="ts">
import Button from 'primevue/button'
import Popover from 'primevue/popover'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { useConfirm } from 'primevue/useconfirm'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import InlineField from '@/components/InlineField.vue'
import ColorPicker from '@/components/items/ColorPicker.vue'
import GateDecision from '@/components/items/GateDecision.vue'
import type { MarkdownFile } from '@/components/markdown/types'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import { useNotify } from '@/composables/useNotify'
import {
  countsAsDone,
  dodProgress,
  isMilestoneOverdue,
  kindLabels,
  overrideMessage,
  progressText,
  statusOptions,
} from '@/domain/milestones'
import { deleteMilestone, moveMilestone, updateMilestone, type MilestoneChange } from '@/local/milestones'
import { repo } from '@/local/repo'
import type { AssignmentRow, DodItemRow, ItemRow, MilestoneRow, MilestoneStatus, Stored } from '@/local/rows'
import { formatDay } from '@/views/items/itemList'
import DodChecklist from './DodChecklist.vue'
import MilestonePersons from './MilestonePersons.vue'

/**
 * One milestone or gate of a project (docs/01 §11): title, kind, status (*done* only with a complete DoD – a gate also
 * with a decision – or after the user confirms an override, which the History records as the status change), due date
 * with *overdue*, colour (default the project's), description (Markdown, edited on its own page), the persons
 * responsible, the Definition of Done and – gates – the decision. Moved up/down, deleted from the ⋯ menu. Read-only
 * without *edit*.
 */
const props = defineProps<{
  milestone: Stored<MilestoneRow>
  project: ItemRow
  dod: readonly Stored<DodItemRow>[]
  assignments: readonly Stored<AssignmentRow>[]
  canEdit: boolean
  today: string
  files: readonly MarkdownFile[]
  first: boolean
  last: boolean
  selected?: boolean
}>()
const router = useRouter()
const notify = useNotify()
const confirm = useConfirm()

const progress = computed(() => dodProgress(props.dod))
const overdue = computed(() => isMilestoneOverdue(props.milestone, props.today))
const done = computed(() => countsAsDone(props.milestone, props.dod))
const color = computed(() => props.milestone.color ?? props.project.color ?? null)
const gate = computed(() => props.milestone.kind === 'gate')
const titleId = computed(() => `milestone-${props.milestone.id}-title`)

async function save(change: MilestoneChange) {
  await updateMilestone(repo, props.milestone.id, change)
}

async function trySave(change: MilestoneChange, failure: string) {
  try {
    await save(change)
  } catch (e) {
    notify.error(e, failure)
  }
}

function setStatus(status: MilestoneStatus | null) {
  if (!status || status === props.milestone.status) return
  const override = status === 'done' ? overrideMessage(props.milestone, props.dod) : null
  if (!override) {
    void trySave({ status }, 'Status not changed')
    return
  }
  confirm.require({
    header: `Mark ${props.milestone.title} done?`,
    message: override,
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Mark done anyway', severity: 'warn' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: () => void trySave({ status }, 'Status not changed'),
  })
}

const dateError = (value: unknown) => {
  const next = typeof value === 'string' && value ? value : null
  if (next && !/^\d{4}-\d{2}-\d{2}$/.test(next)) throw new Error('A date.')
  return next
}

const colorPanel = ref<InstanceType<typeof Popover> | null>(null)
function setColor(value: string | null) {
  colorPanel.value?.hide()
  void trySave({ color: value }, 'Colour not changed')
}

function remove() {
  confirm.require({
    header: `Delete ${props.milestone.title}?`,
    message: 'Its Definition of Done and its persons go with it.',
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Delete', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: async () => {
      try {
        await deleteMilestone(repo, props.milestone.id)
      } catch (e) {
        notify.error(e, 'Not deleted')
      }
    },
  })
}

async function move(step: -1 | 1) {
  try {
    await moveMilestone(repo, props.project.id, props.milestone.id, step)
  } catch (e) {
    notify.error(e, 'Not moved')
  }
}

const more = computed<MenuItem[]>(() => [
  { label: gate.value ? 'Delete gate…' : 'Delete milestone…', icon: 'pi pi-trash', command: remove },
])
</script>

<template>
  <section
    :id="`milestone-${milestone.id}`"
    class="panel milestone"
    :class="{ selected, done, cancelled: milestone.status === 'cancelled' }"
    :style="color ? { '--milestone': color } : undefined"
    :aria-labelledby="titleId"
    :data-testid="`milestone-${milestone.id}`"
  >
    <div class="panel-header head">
      <span class="marker" :class="{ gate }" aria-hidden="true" />
      <Tag :value="kindLabels[milestone.kind]" :severity="gate ? 'contrast' : 'secondary'" />
      <span :id="titleId" class="title">
        <InlineField
          :value="milestone.title"
          :save="(v) => save({ title: String(v ?? '') })"
          :label="`Title of ${milestone.title}`"
          field="title"
          :readonly="!canEdit"
        />
      </span>
      <span class="spacer" />
      <span v-if="progress.total" class="muted progress" :title="`Definition of Done: ${progressText(progress)}`">
        <i class="pi pi-list-check" aria-hidden="true" /> {{ progressText(progress) }}
      </span>
      <i
        v-if="milestone._local?.pending"
        v-tooltip.top="'Not synced yet'"
        role="img"
        class="pi pi-cloud-upload muted"
        aria-label="Not synced yet"
      />
      <template v-if="canEdit">
        <Button
          icon="pi pi-arrow-up"
          size="small"
          text
          severity="secondary"
          :disabled="first"
          :aria-label="`Move ${milestone.title} up`"
          @click="move(-1)"
        />
        <Button
          icon="pi pi-arrow-down"
          size="small"
          text
          severity="secondary"
          :disabled="last"
          :aria-label="`Move ${milestone.title} down`"
          @click="move(1)"
        />
        <MoreMenu :items="more" :label="`More actions for ${milestone.title}`" />
      </template>
    </div>
    <div class="panel-body body">
      <dl class="facts">
        <dt>Status</dt>
        <dd>
          <Select
            v-if="canEdit"
            :model-value="milestone.status"
            :options="[...statusOptions]"
            option-label="label"
            option-value="value"
            size="small"
            :aria-label="`Status of ${milestone.title}`"
            class="status"
            @update:model-value="setStatus"
          />
          <span v-else>{{ statusOptions.find((o) => o.value === milestone.status)?.label }}</span>
          <small v-if="milestone.status === 'done' && !done" class="warn"
            >Done without {{ progress.complete ? 'a decision' : 'a complete DoD' }}</small
          >
        </dd>
        <dt>Due</dt>
        <dd class="due" :class="{ late: overdue }">
          <InlineField
            :value="milestone.dueDate"
            :save="(v) => save({ dueDate: dateError(v) })"
            type="date"
            :label="`Due date of ${milestone.title}`"
            field="dueDate"
            :format="(v) => formatDay(String(v))"
            :readonly="!canEdit"
          />
          <span v-if="overdue" class="overdue"><i class="pi pi-clock" aria-hidden="true" /> Overdue</span>
        </dd>
        <dt>Colour</dt>
        <dd class="color-line">
          <span class="swatch" :style="{ background: color ?? 'transparent' }" aria-hidden="true" />
          <span>{{ milestone.color ?? (project.color ? 'The project’s' : 'Default') }}</span>
          <Button
            v-if="canEdit"
            label="Change"
            size="small"
            text
            :aria-label="`Change the colour of ${milestone.title}`"
            @click="(e: Event) => colorPanel?.toggle(e)"
          />
        </dd>
      </dl>
      <div class="description">
        <div class="sub-head">
          <span class="label">Description</span>
          <Button
            v-if="canEdit"
            label="Edit"
            icon="pi pi-pencil"
            size="small"
            text
            :aria-label="`Edit the description of ${milestone.title}`"
            @click="router.push({ name: 'milestone-edit', params: { id: project.id, mid: milestone.id } })"
          />
        </div>
        <MarkdownView v-if="milestone.description" :source="milestone.description" :files="files" />
        <p v-else class="muted empty">No description.</p>
      </div>
      <MilestonePersons
        :milestone-id="milestone.id"
        :kind="milestone.kind"
        :assignments="assignments"
        :can-edit="canEdit"
      />
      <DodChecklist :milestone-id="milestone.id" :items="dod" :can-edit="canEdit" />
      <GateDecision v-if="gate" :milestone="milestone" :project="project" :can-edit="canEdit" />
    </div>
    <Popover ref="colorPanel">
      <ColorPicker :model-value="milestone.color" :id-prefix="`ms-color-${milestone.id}`" @change="setColor" />
    </Popover>
  </section>
</template>

<style scoped>
.milestone {
  --milestone: var(--p-primary-color);
  border-left: 3px solid var(--milestone);
}

.milestone.selected {
  box-shadow: 0 0 0 2px var(--p-primary-color);
}

.milestone.cancelled {
  opacity: 0.7;
}

.head {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.marker {
  width: 0.75rem;
  height: 0.75rem;
  border-radius: 50%;
  border: 2px solid var(--milestone);
  flex: none;
}

.marker.gate {
  border-radius: 1px;
  transform: rotate(45deg) scale(0.85);
}

.milestone.done .marker {
  background: var(--milestone);
}

.title {
  min-width: 0;
  font-weight: 600;
}

.spacer {
  flex: 1;
}

.progress {
  font-size: 0.857rem;
  white-space: nowrap;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.status {
  min-width: 9rem;
}

.due,
.color-line {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.late :deep(.shown .text),
.overdue {
  color: var(--pgpm-fail);
  font-weight: 600;
}

.warn {
  margin-left: var(--pgpm-space-2);
  color: var(--pgpm-warn);
}

.swatch {
  width: 1rem;
  height: 1rem;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
}

.sub-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.label {
  font-weight: 600;
}

.empty {
  margin: 0;
}
</style>
