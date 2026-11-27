<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import MultiSelect from 'primevue/multiselect'
import Popover from 'primevue/popover'
import Tag from 'primevue/tag'
import ToggleButton from 'primevue/togglebutton'
import { computed, ref, watch, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import FilterBar from '@/components/FilterBar.vue'
import ColumnChooser from '@/components/items/ColumnChooser.vue'
import SavedViewMenu from '@/components/items/SavedViewMenu.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useLocalTable, type ViewState } from '@/composables/useLocalTable'
import { useNow } from '@/composables/useNow'
import { decisionOptions, isDecided, isMilestoneOverdue, progressText, statusLabels } from '@/domain/milestones'
import { localDb } from '@/local/db'
import { today } from '@/local/items'
import { useGroups, usePersons, useUserPersons } from '@/local/lookups'
import {
  itemKey,
  type AssignmentRow,
  type DodItemRow,
  type ItemRow,
  type ItemShareRow,
  type MilestoneRow,
  type ProjectTemplateRow,
  type SavedViewRow,
  type Stored,
} from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'
import { formatDay, groupBy } from '@/views/items/itemList'
import {
  cleanGateFilters,
  countGateFilters,
  decisionText,
  deciderNames,
  defaultGateColumns,
  gateColumns,
  gateProgress,
  gateSortValue,
  gateText,
  matchGate,
  readGateFilters,
  visibleGateColumns,
  type GateContext,
  type GateFilters,
} from './gateList'

/**
 * *Gate decisions* (docs/01 §11, docs/04 F4): every gate of the projects on this device – the stop / go / scale record
 * – searched (gate, project key and title, note), filtered (decision, template, shared with, assigned to me, overdue),
 * sorted by one or more columns, with the columns chosen and saved views of the type `gate` (the seeded *Gate
 * decisions*: decided gates, newest first). All in memory, offline; the state is remembered as `list:gate`. A row opens
 * the project's Milestones tab.
 */
type Gate = Stored<MilestoneRow>

const route = useRoute()
const router = useRouter()
const session = useSession()
const groups = useGroups()
const persons = usePersons()
const users = useUserPersons()
const now = useNow()

const all =
  <T,>(table: string) =>
  async () =>
    ((await localDb().synced(table).toArray()) as unknown as (T & { deleted?: boolean })[]).filter((r) => !r.deleted)
const projects = useLive(
  async () =>
    ((await localDb().synced('items').where('type').equals('project').toArray()) as unknown as ItemRow[]).filter(
      (p) => !p.deleted,
    ),
  [] as ItemRow[],
)
const dod = useLive(all<DodItemRow>('dod_items'), [] as DodItemRow[])
const assignments = useLive(
  async () =>
    (await all<AssignmentRow>('assignments')())
      .filter((a) => !!a.milestoneId)
      .sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : 1)),
  [] as AssignmentRow[],
)
const shares = useLive(all<ItemShareRow>('item_shares'), [] as ItemShareRow[])
const templates = useLive(all<ProjectTemplateRow>('project_templates'), [] as ProjectTemplateRow[])

const context = computed<GateContext>(() => ({
  projects: new Map(projects.value.value.map((p) => [p.id, p])),
  dod: groupBy(dod.value.value, (d) => d.milestoneId),
  assignments: groupBy(assignments.value.value, (a) => a.milestoneId),
  shares: groupBy(shares.value.value, (s) => s.itemId),
  persons: persons.byId.value,
  userName: (id) => users.nameOf(id, ''),
  me: { personId: session.personId },
  today: today(now.value),
}))

// --- filters, columns, view ---
const query = route.query
const text = (v: unknown) => (typeof v === 'string' && v ? v : undefined)
const filters = ref<GateFilters>(
  readGateFilters({
    decisions: text(query.decision)?.split(','),
    assignedToMe: query.me === '1',
    overdue: query.overdue === '1',
  }),
)
const cleaned = computed(() => cleanGateFilters(filters.value))
const columns = ref<string[]>([...defaultGateColumns])
const viewId = ref<string | null>(text(query.view) ?? null)
const urlQuery = computed(() => ({
  decision: cleaned.value.decisions?.join(','),
  me: cleaned.value.assignedToMe ? '1' : undefined,
  overdue: cleaned.value.overdue ? '1' : undefined,
  view: viewId.value ?? undefined,
}))

const table = useLocalTable<Gate>({
  rows: async () =>
    ((await localDb().synced('milestones').toArray()) as unknown as Gate[]).filter(
      (m) => !m.deleted && m.kind === 'gate',
    ),
  text: (m) => gateText(m, context.value),
  filters: cleaned as unknown as Ref<Record<string, unknown>>,
  match: (m, f) => matchGate(m, f as GateFilters, context.value),
  sortValue: (m, field) => gateSortValue(m, field, context.value),
  defaultSort: '-decided_at',
  multiSort: true,
  syncUrl: true,
  urlQuery,
  settingsKey: 'list:gate',
  remember: () => cleanGateFilters(filters.value) as Record<string, unknown>,
  restore: (f) => (filters.value = readGateFilters(f)),
  columns,
  viewId,
  pageSize: 50,
})

// A link to a view alone (`?view=<id>`): its definition, once it is on the device.
const linkedView = Object.keys(query).length === 1 ? text(query.view) : undefined
if (linkedView) {
  const row = useLive(
    async () => (await localDb().synced('saved_views').get(linkedView)) as unknown as SavedViewRow | undefined,
    undefined as SavedViewRow | undefined,
  )
  const stop = watch(row.value, (view) => {
    if (!view || view.deleted) return
    table.applyState(view.definition as ViewState)
    viewId.value = view.id
    stop()
  })
}

function applyView(state: ViewState | null) {
  if (state) {
    table.applyState(state)
    if (!state.columns?.length) columns.value = [...defaultGateColumns]
  } else {
    table.applyState({ search: '', filters: {}, sort: [], columns: [...defaultGateColumns] })
  }
}

const currentState = computed(() => table.currentState())
const shownColumns = computed(() => visibleGateColumns(columns.value))
const decisionChoices = decisionOptions.map((o) => ({ label: o.label, value: o.value }))
const templateChoices = computed(() =>
  templates.value.value.map((t) => ({ label: t.isArchived ? `${t.name} (archived)` : t.name, value: t.id })),
)
const groupChoices = computed(() => groups.value.map((g) => ({ label: g.name, value: g.id })))
const quick: (keyof GateFilters)[] = ['decisions', 'assignedToMe', 'overdue']
const furtherCount = computed(() => countGateFilters(filters.value, quick))
const activeCount = computed(() => countGateFilters(filters.value))
const filtersPanel = ref<InstanceType<typeof Popover> | null>(null)

function setFilter<K extends keyof GateFilters>(key: K, value: GateFilters[K]) {
  filters.value = { ...filters.value, [key]: value }
}

const severity = (m: Gate) => decisionOptions.find((o) => o.value === (m.decision ?? 'pending'))?.severity
const projectOf = (m: Gate) => context.value.projects.get(m.itemId)
const open = (m: Gate) => router.push({ name: 'project', params: { id: m.itemId, tab: 'milestones' } })
const decidedOn = (m: Gate) => {
  const at = gateSortValue(m, 'decided_at', context.value)
  return typeof at === 'string' ? formatDay(at.slice(0, 10)) : ''
}
</script>

<template>
  <div class="page">
    <PageHeader title="Gate decisions" :crumbs="[{ label: 'Projects', to: { name: 'projects' } }]" />
    <FilterBar v-model:search="table.search.value" placeholder="Search gates and projects…" :active="activeCount">
      <MultiSelect
        :model-value="filters.decisions ?? []"
        :options="decisionChoices"
        option-label="label"
        option-value="value"
        placeholder="Any decision"
        :max-selected-labels="2"
        size="small"
        aria-label="Decision"
        class="decision-filter"
        @update:model-value="(v: GateFilters['decisions']) => setFilter('decisions', v)"
      />
      <ToggleButton
        :model-value="!!filters.assignedToMe"
        on-label="Assigned to me"
        off-label="Assigned to me"
        on-icon="pi pi-user"
        off-icon="pi pi-user"
        size="small"
        @update:model-value="(v: boolean) => setFilter('assignedToMe', v || undefined)"
      />
      <ToggleButton
        :model-value="!!filters.overdue"
        on-label="Overdue"
        off-label="Overdue"
        on-icon="pi pi-clock"
        off-icon="pi pi-clock"
        size="small"
        @update:model-value="(v: boolean) => setFilter('overdue', v || undefined)"
      />
      <Button
        :label="furtherCount ? `Filters (${furtherCount})` : 'Filters'"
        icon="pi pi-filter"
        size="small"
        severity="secondary"
        :outlined="!furtherCount"
        aria-haspopup="dialog"
        @click="(e: Event) => filtersPanel?.toggle(e)"
      />
      <Popover ref="filtersPanel">
        <div class="form further" role="dialog" aria-label="More filters">
          <div class="field">
            <label for="gate-templates">Template</label>
            <MultiSelect
              :model-value="filters.templateIds ?? []"
              input-id="gate-templates"
              :options="templateChoices"
              option-label="label"
              option-value="value"
              placeholder="Any template"
              size="small"
              @update:model-value="(v: string[]) => setFilter('templateIds', v)"
            />
          </div>
          <div class="field">
            <label for="gate-groups">Shared with</label>
            <MultiSelect
              :model-value="filters.groupIds ?? []"
              input-id="gate-groups"
              :options="groupChoices"
              option-label="label"
              option-value="value"
              placeholder="Any group"
              size="small"
              @update:model-value="(v: string[]) => setFilter('groupIds', v)"
            />
          </div>
        </div>
      </Popover>
      <Button v-if="activeCount" label="Clear" icon="pi pi-filter-slash" size="small" text @click="filters = {}" />
      <template #end>
        <SavedViewMenu v-model="viewId" type="gate" plural="Gates" :state="currentState" @apply="applyView" />
        <ColumnChooser v-model="columns" :columns="gateColumns" :defaults="defaultGateColumns" />
      </template>
    </FilterBar>
    <ListTable :table="table" what="gates" fixed @select="open">
      <Column
        v-for="col in shownColumns"
        :key="col.id"
        :field="col.id"
        :header="col.label"
        sortable
        :style="col.width ? { width: col.width } : undefined"
        :body-class="col.align === 'right' ? 'numeric' : undefined"
      >
        <template #body="{ data }">
          <span v-if="col.id === 'project'" class="ellipsis">
            <template v-if="projectOf(data)">
              <span class="mono muted">{{ itemKey(projectOf(data)!) }}</span> {{ projectOf(data)!.title }}
            </template>
          </span>
          <span v-else-if="col.id === 'title'" class="title-cell">
            <RouterLink
              :to="{ name: 'project', params: { id: data.itemId, tab: 'milestones' } }"
              class="strong ellipsis"
              @click.stop
              >{{ data.title }}</RouterLink
            >
            <i
              v-if="data._local?.pending"
              v-tooltip.top="'Not synced yet'"
              class="pi pi-cloud-upload muted"
              aria-label="Not synced yet"
            />
          </span>
          <span v-else-if="col.id === 'status'">{{ statusLabels[data.status as MilestoneRow['status']] }}</span>
          <span
            v-else-if="col.id === 'due_date'"
            :class="{ late: isMilestoneOverdue(data, context.today) }"
            :title="isMilestoneOverdue(data, context.today) ? 'Overdue' : undefined"
            >{{ formatDay(data.dueDate) }}</span
          >
          <Tag
            v-else-if="col.id === 'decision'"
            :value="decisionText(data.decision)"
            :severity="severity(data)"
            :class="{ undecided: !isDecided(data.decision) }"
          />
          <span v-else-if="col.id === 'decided_at'">{{ decidedOn(data) }}</span>
          <span v-else-if="col.id === 'decided_by'" class="ellipsis">{{
            isDecided(data.decision) ? users.nameOf(data.decidedBy, data.decidedBy ? '–' : 'Not synced yet') : ''
          }}</span>
          <span v-else-if="col.id === 'dod_progress'">{{ progressText(gateProgress(data, context)) }}</span>
          <span v-else-if="col.id === 'persons'" class="ellipsis">{{ deciderNames(data, context).join(', ') }}</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'project', params: { id: item.itemId, tab: 'milestones' } }" class="mobile-row">
          <span class="strong ellipsis">{{ item.title }}</span>
          <span class="muted small">
            <template v-if="projectOf(item)">{{ itemKey(projectOf(item)!) }} {{ projectOf(item)!.title }} · </template>
            {{ decisionText(item.decision) }}
            <template v-if="decidedOn(item)"> · {{ decidedOn(item) }}</template>
          </span>
        </RouterLink>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.decision-filter {
  min-width: 10rem;
}

.further {
  min-width: 16rem;
}

.title-cell {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.late {
  color: var(--pgpm-fail);
  font-weight: 600;
}

.undecided {
  opacity: 0.75;
}

.small {
  font-size: 0.857rem;
}

:deep(td.numeric) {
  text-align: right;
}

.mobile-row {
  display: flex;
  flex-direction: column;
  color: inherit;
  text-decoration: none;
}
</style>
