<script setup lang="ts">
import Button from 'primevue/button'
import Popover from 'primevue/popover'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import EmptyState from '@/components/EmptyState.vue'
import AttachmentsPanel from '@/components/files/AttachmentsPanel.vue'
import CoverImage from '@/components/files/CoverImage.vue'
import InlineField from '@/components/InlineField.vue'
import AssignmentsPanel from '@/components/items/AssignmentsPanel.vue'
import ColorPicker from '@/components/items/ColorPicker.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { itemRoute, itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { useMarkdownFiles } from '@/local/files'
import { daysInStatus, overdueReason, selectableStatuses } from '@/local/items'
import { useGroups, useStatuses, useUserPersons } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow, type ItemShareRow, type ProjectTemplateRow, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { formatDay, formatValue } from './itemList'

/**
 * An item's *Overview* tab (docs/01 §4.4, §7–§10): the description rendered with *Edit* to its editing page, the
 * sections (phase 4 fills the `#sections` slot), the attachments; beside it the fixed fields of the type edited inline
 * (title, status, colour, pinned, the lead's and the project's fields), the cover image, the assigned persons and the
 * facts. Every change is a patch of the local data (offline too); with *read* access it is all read-only.
 */
const props = defineProps<{
  item: Stored<ItemRow>
  canEdit: boolean
  shares: readonly ItemShareRow[]
  /** The project it was converted into (ideas, leads). */
  converted?: Stored<ItemRow> | null
  /** The idea or lead it was converted from (projects); null: not on this device. */
  origin?: Stored<ItemRow> | null
}>()
const router = useRouter()
const notify = useNotify()
const now = useNow()
const statuses = useStatuses()
const groups = useGroups()
const people = useUserPersons()
const files = useMarkdownFiles(() => props.item.id)
const info = computed(() => itemTypes[props.item.type])

const template = useLive(
  async () =>
    props.item.projectTemplateId
      ? ((await localDb().synced('project_templates').get(props.item.projectTemplateId)) as unknown as
          ProjectTemplateRow | undefined)
      : undefined,
  undefined as ProjectTemplateRow | undefined,
  () => props.item.projectTemplateId,
)

const status = computed(() => (props.item.statusId ? statuses.byId.value.get(props.item.statusId) : undefined))
const options = computed(() => selectableStatuses(statuses.all.value, props.item.type, props.item.statusId))
const overdue = computed(() => overdueReason(props.item, status.value, now.value))
const days = computed(() => daysInStatus(props.item, now.value))
const shareList = computed(() =>
  props.shares.map((s) => ({
    name: groups.value.find((g) => g.id === s.groupId)?.name ?? 'Unknown group',
    access: s.access,
  })),
)

function invalid(field: string, message: string): never {
  throw new ApiError(400, { code: 'validation_failed', errors: { [field]: [message] } })
}

async function save(values: Record<string, unknown>) {
  await repo.patch('items', props.item.id, values)
}

async function saveTitle(value: unknown) {
  const title = String(value ?? '').trim()
  if (!title || title.length > 300) invalid('title', title ? 'At most 300 characters.' : 'A title is needed.')
  await save({ title })
}

const text =
  (field: keyof ItemRow, max = 300) =>
  async (value: unknown) => {
    const next = typeof value === 'string' && value.trim() ? value.trim() : null
    if (next && next.length > max) invalid(field, `At most ${max} characters.`)
    await save({ [field]: next })
  }

const date = (field: keyof ItemRow) => async (value: unknown) => {
  const next = typeof value === 'string' && value ? value : null
  if (next && !/^\d{4}-\d{2}-\d{2}$/.test(next)) invalid(field, 'A date.')
  if (field === 'projectEndDate' && next && props.item.projectStartDate && next < props.item.projectStartDate)
    invalid(field, 'Not before the start.')
  await save({ [field]: next })
}

async function saveValue(value: unknown) {
  if (value !== null && (typeof value !== 'number' || value < 0)) invalid('leadValue', 'An amount, 0 or more.')
  await save({ leadValue: value === null ? null : Math.round((value as number) * 100) / 100 })
}

async function saveCurrency(value: unknown) {
  const code = typeof value === 'string' && value.trim() ? value.trim().toUpperCase() : null
  if (code && !/^[A-Z]{3}$/.test(code)) invalid('leadCurrency', 'An ISO 4217 code (CHF).')
  await save({ leadCurrency: code })
}

async function saveProbability(value: unknown) {
  if (value !== null && (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 100))
    invalid('leadProbability', 'A whole number from 0 to 100.')
  await save({ leadProbability: value })
}

async function setStatus(id: string | null) {
  if (!id || id === props.item.statusId) return
  try {
    await save({ statusId: id })
  } catch (e) {
    notify.error(e, 'Status not changed')
  }
}

const colorPanel = ref<InstanceType<typeof Popover> | null>(null)
async function setColor(color: string | null) {
  try {
    await save({ color })
    colorPanel.value?.hide()
  } catch (e) {
    notify.error(e, 'Colour not changed')
  }
}

const due = computed(
  () =>
    props.item.type === 'lead' &&
    !!props.item.leadNextActionDue &&
    status.value?.category !== 'closed' &&
    !!overdue.value?.startsWith('Next action'),
)

type Value = string | number | boolean | null
interface Fixed {
  key: keyof ItemRow
  label: string
  type?: 'text' | 'number' | 'date' | 'bool'
  save: (value: Value) => Promise<unknown>
  format?: (value: Value) => string
  mono?: boolean
  warn?: boolean
}

const day = (v: Value) => formatDay(String(v))

/** The type's fixed fields (docs/01 §7, §9, §10.1), each edited inline. */
const fixed = computed<Fixed[]>(() => {
  switch (props.item.type) {
    case 'note':
      return [{ key: 'pinned', label: 'Pinned', type: 'bool', save: (v) => save({ pinned: v === true }) }]
    case 'lead':
      return [
        { key: 'leadOrganisation', label: 'Organisation', save: text('leadOrganisation') },
        {
          key: 'leadValue',
          label: 'Estimated value',
          type: 'number',
          save: saveValue,
          format: (v) => formatValue(v as number, props.item.leadCurrency),
        },
        { key: 'leadCurrency', label: 'Currency', save: saveCurrency, mono: true },
        {
          key: 'leadProbability',
          label: 'Probability',
          type: 'number',
          save: saveProbability,
          format: (v) => `${v} %`,
        },
        {
          key: 'leadDecisionDate',
          label: 'Expected decision date',
          type: 'date',
          save: date('leadDecisionDate'),
          format: day,
        },
        { key: 'leadNextAction', label: 'Next action', save: text('leadNextAction', 500) },
        {
          key: 'leadNextActionDue',
          label: 'Next action due',
          type: 'date',
          save: date('leadNextActionDue'),
          format: day,
          warn: due.value,
        },
        { key: 'leadSource', label: 'Source', save: text('leadSource') },
      ]
    case 'project':
      return [
        { key: 'projectStartDate', label: 'Start date', type: 'date', save: date('projectStartDate'), format: day },
        { key: 'projectEndDate', label: 'End date', type: 'date', save: date('projectEndDate'), format: day },
      ]
    default:
      return []
  }
})
</script>

<template>
  <div class="two-columns overview">
    <div class="column">
      <section class="panel description" aria-labelledby="description-title">
        <div class="panel-header with-action">
          <span id="description-title">{{ info.descriptionLabel }}</span>
          <Button
            v-if="canEdit"
            label="Edit"
            icon="pi pi-pencil"
            size="small"
            text
            :aria-label="`Edit ${info.descriptionLabel.toLowerCase()}`"
            @click="router.push({ name: info.edit, params: { id: item.id } })"
          />
        </div>
        <div class="panel-body">
          <MarkdownView v-if="item.description" :source="item.description" :files="files" />
          <EmptyState v-else icon="pi pi-align-left" :title="`No ${info.descriptionLabel.toLowerCase()} yet.`" />
        </div>
      </section>
      <slot name="sections" :item="item" :can-edit="canEdit" />
      <AttachmentsPanel :item-id="item.id" :can-edit="canEdit" />
    </div>
    <div class="column">
      <section class="panel" aria-labelledby="details-title">
        <div id="details-title" class="panel-header">Details</div>
        <div class="panel-body">
          <dl class="facts">
            <dt>Title</dt>
            <dd>
              <InlineField :value="item.title" :save="saveTitle" field="title" label="Title" :readonly="!canEdit" />
            </dd>
            <template v-if="info.hasStatus">
              <dt>Status</dt>
              <dd class="status-line">
                <Select
                  v-if="canEdit"
                  :model-value="item.statusId"
                  :options="options"
                  option-label="name"
                  option-value="id"
                  size="small"
                  aria-label="Status"
                  placeholder="No status"
                  class="status-select"
                  @update:model-value="setStatus"
                >
                  <template #value>
                    <StatusBadge :status="status" />
                  </template>
                  <template #option="{ option }">
                    <StatusBadge :status="option" />
                  </template>
                </Select>
                <StatusBadge v-else :status="status" />
                <StatusBadge v-if="overdue" :overdue="overdue" />
                <small v-if="days !== null" class="muted">{{ days === 1 ? '1 day' : `${days} days` }} in status</small>
              </dd>
            </template>
            <dt>Colour</dt>
            <dd class="color-line">
              <span class="swatch" :style="{ background: item.color ?? 'transparent' }" aria-hidden="true" />
              <span>{{ item.color ?? 'Default' }}</span>
              <Button
                v-if="canEdit"
                label="Change"
                size="small"
                text
                aria-label="Change colour"
                @click="(e: Event) => colorPanel?.toggle(e)"
              />
            </dd>
            <template v-for="f in fixed" :key="f.key">
              <dt>{{ f.label }}</dt>
              <dd :class="{ due: f.warn }">
                <InlineField
                  :value="(item[f.key] ?? null) as Value"
                  :save="f.save"
                  :type="f.type ?? 'text'"
                  :field="f.key"
                  :label="f.label"
                  :format="f.format ?? null"
                  :mono="f.mono"
                  :readonly="!canEdit"
                />
              </dd>
            </template>
            <template v-if="item.type === 'project'">
              <dt>Template</dt>
              <dd>
                {{ template.value.value?.name ?? (item.projectTemplateId ? 'Unknown template' : 'None (blank)') }}
              </dd>
              <template v-if="item.originItemId">
                <dt>Origin</dt>
                <dd>
                  <RouterLink v-if="origin" :to="itemRoute(origin)"
                    >{{ itemKey(origin) }} {{ origin.title }}</RouterLink
                  >
                  <span v-else class="muted"><i class="pi pi-lock" aria-hidden="true" /> Restricted</span>
                </dd>
              </template>
            </template>
            <template v-if="converted">
              <dt>Converted to</dt>
              <dd>
                <RouterLink :to="itemRoute(converted)">{{ itemKey(converted) }} {{ converted.title }}</RouterLink>
              </dd>
            </template>
            <dt>Key</dt>
            <dd class="mono">{{ item.number === null ? 'Assigned at the first sync' : itemKey(item) }}</dd>
            <dt>Owner</dt>
            <dd>{{ people.nameOf(item.ownerUserId, '–') }}</dd>
            <dt>Shared with</dt>
            <dd>
              <span v-if="!shareList.length" class="muted">Nobody (private)</span>
              <span v-for="s in shareList" :key="s.name" class="share"
                >{{ s.name }} <small class="muted">{{ s.access }}</small></span
              >
            </dd>
            <dt>Created</dt>
            <dd><RelativeTime :value="item.createdAt" /> · {{ people.nameOf(item.createdBy, '–') }}</dd>
            <dt>Modified</dt>
            <dd><RelativeTime :value="item.updatedAt" /> · {{ people.nameOf(item.updatedBy, '–') }}</dd>
          </dl>
        </div>
      </section>
      <section class="panel" aria-labelledby="cover-title">
        <div id="cover-title" class="panel-header">Cover image</div>
        <div class="panel-body">
          <CoverImage :item-id="item.id" :image-file-id="item.imageFileId" :can-edit="canEdit" />
        </div>
      </section>
      <AssignmentsPanel :item-id="item.id" :can-edit="canEdit" />
    </div>
    <Popover ref="colorPanel">
      <ColorPicker :model-value="item.color" id-prefix="item-color" @change="setColor" />
    </Popover>
  </div>
</template>

<style scoped>
.overview {
  align-items: start;
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-4);
  min-width: 0;
}

.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.description .panel-body {
  min-height: 8rem;
}

.status-line,
.color-line {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.status-select {
  min-width: 10rem;
}

.swatch {
  width: 1rem;
  height: 1rem;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
}

.due :deep(.shown .text) {
  color: var(--pgpm-warn);
  font-weight: 600;
}

.share {
  display: inline-block;
  margin-right: var(--pgpm-space-3);
}
</style>
