<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import type { ShareSpec } from '@/api/sync'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { defaultStatus, selectableStatuses, titleError } from '@/local/items'
import { useGroups, useStatuses } from '@/local/lookups'
import { itemKey, type ItemRow, type ItemType } from '@/local/rows'
import { itemRank } from '@/local/search'
import { useLive } from '@/local/useLive'
import { DECISION_LABELS, MILESTONE_STATUS_LABELS } from '@/maps/cards'
import type { GateDecision, MilestoneStatus } from '@/maps/types'
import { useSession } from '@/stores/session'
import type { MapRequests } from './mapRequests'

/**
 * The map page's dialog for what a placing tool asks (mapRequests.ts): an item tool's *Create new* (title, status,
 * *Share with* preselected with the creator's groups at *edit*) and *Add existing* (a local search; items already on the
 * map are marked with their count and can be added again); a project for a timeline; a project, then one of its
 * milestones or gates, for the milestone tool. Offline too.
 */
const props = defineProps<{ requests: MapRequests; counts: ReadonlyMap<string, number> }>()
const session = useSession()
const notify = useNotify()
const groups = useGroups()
const statuses = useStatuses()

interface MilestoneRecord {
  id: string
  itemId: string
  kind: 'milestone' | 'gate'
  title: string
  status: MilestoneStatus
  decision: GateDecision | null
  dueDate: string | null
  sortOrder: string
  deleted?: boolean
}

const pending = computed(() => props.requests.pending.value)
const type = computed<ItemType>(() => {
  const p = pending.value
  return p?.kind === 'item' ? p.request.extra.type : 'project'
})
const info = computed(() => itemTypes[type.value])
const header = computed(() => {
  const p = pending.value
  if (!p) return ''
  if (p.kind === 'item') return `Add ${info.value.label}`
  return p.kind === 'milestone' ? 'Add a milestone or gate' : 'Add a project timeline'
})

const query = ref('')
const title = ref('')
const titleProblem = ref<string | null>(null)
const statusId = ref<string | null>(null)
const shares = ref<ShareSpec[]>([])
const creating = ref(false)
const project = ref<ItemRow | null>(null)

const items = useLive(
  async () =>
    ((await localDb().synced('items').where('type').equals(type.value).toArray()) as unknown as ItemRow[]).filter(
      (i) => !i.deleted,
    ),
  [] as ItemRow[],
  () => type.value,
)
const milestones = useLive(
  async () =>
    project.value
      ? (
          (await localDb()
            .synced('milestones')
            .where('itemId')
            .equals(project.value.id)
            .toArray()) as unknown as MilestoneRecord[]
        )
          .filter((m) => !m.deleted)
          .sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : a.sortOrder > b.sortOrder ? 1 : 0))
      : [],
  [] as MilestoneRecord[],
  () => project.value?.id,
)

const statusOptions = computed(() =>
  info.value.hasStatus ? selectableStatuses(statuses.all.value, type.value).filter((s) => !s.isArchived) : [],
)
const creatorShares = computed<ShareSpec[]>(() =>
  groups.value.filter((g) => session.groupIds.includes(g.id)).map((g) => ({ groupId: g.id, access: 'edit' })),
)

watch(
  pending,
  (p) => {
    if (!p) return
    query.value = ''
    title.value = ''
    titleProblem.value = null
    project.value = null
    shares.value = creatorShares.value.map((s) => ({ ...s }))
    statusId.value = info.value.hasStatus ? (defaultStatus(statuses.all.value, type.value)?.id ?? null) : null
  },
  { immediate: true },
)
watch(creatorShares, (next, before) => {
  if (pending.value && JSON.stringify(shares.value) === JSON.stringify(before ?? [])) shares.value = [...next]
})

const results = computed(() => {
  const q = query.value.trim()
  const list = items.value.value
  if (!q) return [...list].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, 30)
  return list
    .map((item) => ({ item, rank: itemRank(item, q) }))
    .filter((r): r is { item: ItemRow; rank: number } => r.rank !== null)
    .sort((a, b) => a.rank - b.rank || b.item.updatedAt.localeCompare(a.item.updatedAt))
    .slice(0, 30)
    .map((r) => r.item)
})

const canCreate = computed(() => pending.value?.kind === 'item' && session.allows(info.value.create))

async function create() {
  titleProblem.value = titleError(title.value)
  if (titleProblem.value) return
  creating.value = true
  try {
    await props.requests.createAndPlace({ title: title.value, statusId: statusId.value, shares: shares.value })
  } catch (e) {
    notify.error(e, `${info.value.title} not created`)
  } finally {
    creating.value = false
  }
}

function choose(item: ItemRow) {
  if (pending.value?.kind === 'milestone') {
    project.value = item
    return
  }
  props.requests.place(item.id)
}

function visibleChanged(open: boolean) {
  if (!open) props.requests.cancel()
}
</script>

<template>
  <Dialog
    :visible="pending !== null"
    modal
    :header="header"
    :style="{ width: 'min(34rem, 100vw)' }"
    data-testid="place-dialog"
    @update:visible="visibleChanged"
  >
    <form v-if="canCreate" id="place-create" class="form create" novalidate @submit.prevent="create">
      <h3>Create new</h3>
      <div class="field">
        <label for="place-title">Title</label>
        <InputText id="place-title" v-model="title" autofocus maxlength="300" :invalid="!!titleProblem" />
        <small v-if="titleProblem" class="field-error">{{ titleProblem }}</small>
      </div>
      <div v-if="info.hasStatus" class="field">
        <label for="place-status">Status</label>
        <Select
          v-model="statusId"
          input-id="place-status"
          :options="statusOptions"
          option-label="name"
          option-value="id"
          placeholder="Status"
        />
      </div>
      <details class="share">
        <summary>
          Share with ·
          {{ shares.length ? `${shares.length} group${shares.length === 1 ? '' : 's'}` : 'private' }}
        </summary>
        <ShareEditor v-model="shares" :groups="groups" id-prefix="place-share" />
      </details>
      <Button
        type="submit"
        :label="`Create ${info.label}`"
        icon="pi pi-plus"
        size="small"
        :loading="creating"
        :disabled="info.hasStatus && !statusId"
      />
    </form>
    <section class="existing">
      <h3 v-if="pending?.kind === 'milestone' && project">
        <Button icon="pi pi-arrow-left" text size="small" aria-label="Another project" @click="project = null" />
        {{ project.title }}
      </h3>
      <h3 v-else>{{ pending?.kind === 'item' ? 'Add existing' : 'Pick a project' }}</h3>
      <template v-if="pending?.kind === 'milestone' && project">
        <Message v-if="!milestones.value.value.length" severity="secondary" size="small" :closable="false">
          This project has no milestones or gates yet.
        </Message>
        <ul class="choices">
          <li v-for="m in milestones.value.value" :key="m.id">
            <button type="button" @click="requests.place(m.id)">
              <i :class="m.kind === 'gate' ? 'pi pi-stop' : 'pi pi-circle'" aria-hidden="true" />
              <span class="label">{{ m.title }}</span>
              <span class="muted small">
                {{ m.kind === 'gate' && m.decision ? DECISION_LABELS[m.decision] : MILESTONE_STATUS_LABELS[m.status] }}
              </span>
            </button>
          </li>
        </ul>
      </template>
      <template v-else>
        <IconField>
          <InputIcon class="pi pi-search" />
          <InputText
            v-model="query"
            :placeholder="`Search ${itemTypes[type].plural.toLowerCase()}…`"
            aria-label="Search"
          />
        </IconField>
        <ul class="choices">
          <li v-for="item in results" :key="item.id">
            <button type="button" :data-item="item.id" @click="choose(item)">
              <i :class="itemTypes[item.type].icon" aria-hidden="true" />
              <span class="mono muted">{{ itemKey(item) }}</span>
              <span class="label">{{ item.title }}</span>
              <span v-if="counts.get(item.id)" class="on-map">on the map · {{ counts.get(item.id) }}×</span>
            </button>
          </li>
          <li v-if="!results.length" class="muted">Nothing found.</li>
        </ul>
      </template>
    </section>
  </Dialog>
</template>

<style scoped>
h3 {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  margin: 0 0 var(--pgpm-space-2);
  font-size: 0.857rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.create {
  padding-bottom: var(--pgpm-space-3);
  margin-bottom: var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
}

.create > :deep(button[type='submit']) {
  align-self: flex-start;
}

.share summary {
  cursor: pointer;
  font-size: 0.857rem;
  color: var(--pgpm-muted);
  margin-bottom: var(--pgpm-space-2);
}

.existing :deep(.p-iconfield),
.existing :deep(input) {
  width: 100%;
}

.choices {
  list-style: none;
  margin: var(--pgpm-space-2) 0 0;
  padding: 0;
  max-height: 40vh;
  overflow-y: auto;
}

.choices button {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  width: 100%;
  padding: 6px 4px;
  border: 0;
  background: none;
  color: var(--pgpm-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  border-radius: var(--p-border-radius-sm);
}

.choices button:hover {
  background: var(--p-content-hover-background);
}

.label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.on-map {
  font-size: 0.786rem;
  color: var(--p-primary-color);
  white-space: nowrap;
}

.small {
  font-size: 0.857rem;
}
</style>
