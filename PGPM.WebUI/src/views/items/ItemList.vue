<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import MultiSelect from 'primevue/multiselect'
import Popover from 'primevue/popover'
import ToggleButton from 'primevue/togglebutton'
import { computed, ref, watch, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import FieldValue from '@/components/fields/FieldValue.vue'
import FilterBar from '@/components/FilterBar.vue'
import BulkActions from '@/components/items/BulkActions.vue'
import ColumnChooser from '@/components/items/ColumnChooser.vue'
import ItemFiltersPanel from '@/components/items/ItemFiltersPanel.vue'
import SavedViewMenu from '@/components/items/SavedViewMenu.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import FirstSyncNotice from '@/components/sync/FirstSyncNotice.vue'
import { useLocalTable, type ViewState } from '@/composables/useLocalTable'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { timeboxPassed } from '@/domain/fields/renderValue'
import { itemRoute, itemTypes } from '@/domain/itemTypes'
import { useCatalogue } from '@/local/catalogue'
import { localDb } from '@/local/db'
import { oneValuePerField } from '@/local/fields'
import { selectableStatuses, today } from '@/local/items'
import { usePersons, useStatuses, useUserPersons } from '@/local/lookups'
import {
  itemKey,
  type AssignmentRow,
  type FieldDefinitionRow,
  type FieldValueRow,
  type ItemLinkRow,
  type ItemRow,
  type ItemShareRow,
  type ItemType,
  type ProjectTemplateRow,
  type SavedViewRow,
  type Stored,
} from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import QuadrantFilter from '@/views/maps/QuadrantFilter.vue'
import { useItemQuadrants } from '@/views/maps/quadrants'
import { createRequested } from './createRequest'
import ItemCreateDialog from './ItemCreateDialog.vue'
import {
  cleanFilters,
  columnsFor,
  countFilters,
  defaultColumns,
  formatDay,
  formatValue,
  groupBy,
  itemFieldValue,
  itemText,
  linkSummary,
  matchItem,
  personNames,
  readFilters,
  sortValue,
  statusOf,
  visibleColumns,
  type ItemFilters,
  type ListContext,
} from './itemList'

/**
 * An item type's list (docs/01 §4.3): every item of the type on this device – searched (key, title, description),
 * filtered, sorted by one or more columns (Ctrl/⌘-click adds one), with the columns chosen, saved views, bulk actions;
 * all in memory, offline. The state (filters, sort, columns, view) is remembered per user (`user_settings`
 * `list:<type>`) and the quick filters, sort, search and view are in the URL. Pinned notes first.
 */
type Item = Stored<ItemRow>

const props = defineProps<{ type: ItemType }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()
const statuses = useStatuses()
const persons = usePersons()
const users = useUserPersons()
const now = useNow()
const info = itemTypes[props.type]

// --- the lookups the filters and columns need ---
const all =
  <T,>(table: string) =>
  async () =>
    ((await localDb().synced(table).toArray()) as unknown as (T & { deleted?: boolean })[]).filter((r) => !r.deleted)
const assignments = useLive(all<AssignmentRow>('assignments'), [] as AssignmentRow[])
const shares = useLive(all<ItemShareRow>('item_shares'), [] as ItemShareRow[])
const links = useLive(all<ItemLinkRow>('item_links'), [] as ItemLinkRow[])
const types = useLive(async () => {
  const map = new Map<string, ItemType>()
  await localDb()
    .synced('items')
    .each((row) => {
      if (row.deleted !== true) map.set(row.id, row.type as ItemType)
    })
  return map
}, new Map<string, ItemType>())
// Matrix maps that show the type: a quadrant column and filter each (phase 5).
const quadrants = useItemQuadrants(props.type)
const templates = useLive(
  async () => new Map((await all<ProjectTemplateRow>('project_templates')()).map((t) => [t.id, t.name])),
  new Map<string, string>(),
)
const { catalogue } = useCatalogue()
const hasFields = props.type === 'idea' || props.type === 'project'
const fieldValueRows = useLive(
  async () => (hasFields ? oneValuePerField(await all<FieldValueRow>('field_values')()) : []),
  [] as FieldValueRow[],
)
const fieldValues = computed(() => {
  const byItem = new Map<string, Record<string, unknown>>()
  for (const v of fieldValueRows.value.value) {
    const field = catalogue.value.fieldById.get(v.fieldId)
    if (!field) continue
    let values = byItem.get(v.itemId)
    if (!values) byItem.set(v.itemId, (values = {}))
    values[field.key] = v.value
  }
  return byItem
})

const context = computed<ListContext>(() => ({
  statuses: statuses.byId.value,
  assignments: groupBy(
    [...assignments.value.value].sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : 1)),
    (a) => a.itemId,
  ),
  shares: groupBy(shares.value.value, (s) => s.itemId),
  outgoing: groupBy(links.value.value, (l) => l.fromItemId),
  incoming: groupBy(links.value.value, (l) => l.toItemId),
  types: types.value.value,
  persons: persons.byId.value,
  userName: (id) => users.nameOf(id, ''),
  templates: templates.value.value,
  me: { userId: session.user?.id ?? null, personId: session.personId },
  now: now.value,
  catalogue: catalogue.value,
  fieldValues: fieldValues.value,
  scoreCache: new Map(),
  columnValue: quadrants.columnValue,
  quadrantOf: quadrants.quadrantOf,
}))
const renderContext = computed(() => ({ personName: (id: string) => persons.byId.value.get(id)?.displayName }))

// --- filters, columns, view ---
const query = route.query
const text = (v: unknown) => (typeof v === 'string' && v ? v : undefined)
const filters = ref<ItemFilters>(
  readFilters({
    statusIds: text(query.status)?.split(','),
    assignedToMe: query.me === '1',
    overdue: query.overdue === '1',
    pinned: query.pinned === '1',
    mine: query.mine === '1',
  }),
)
const cleaned = computed(() => cleanFilters(filters.value))
const columns = ref<string[]>(defaultColumns(props.type))
const viewId = ref<string | null>(text(query.view) ?? null)
const urlQuery = computed(() => ({
  status: cleaned.value.statusIds?.join(','),
  me: cleaned.value.assignedToMe ? '1' : undefined,
  overdue: cleaned.value.overdue ? '1' : undefined,
  pinned: cleaned.value.pinned ? '1' : undefined,
  mine: cleaned.value.mine ? '1' : undefined,
  view: viewId.value ?? undefined,
}))

/** Phase 2's notes list sorted by member names. */
const legacy: Record<string, string> = { updatedAt: 'updated_at', createdAt: 'created_at' }

const table = useLocalTable<Item>({
  rows: async () => (await localDb().synced('items').where('type').equals(props.type).toArray()) as unknown as Item[],
  text: (item) => itemText(item, context.value),
  filters: cleaned as unknown as Ref<Record<string, unknown>>,
  match: (item, f) => matchItem(item, f as ItemFilters, context.value),
  sortValue: (item, field) => sortValue(item, legacy[field] ?? field, context.value),
  lead: props.type === 'note' ? (a, b) => Number(!!b.pinned) - Number(!!a.pinned) : undefined,
  defaultSort: '-updated_at',
  multiSort: true,
  syncUrl: true,
  urlQuery,
  settingsKey: `list:${props.type}`,
  remember: () => cleanFilters(filters.value) as Record<string, unknown>,
  restore: (f) => (filters.value = readFilters(f)),
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
    if (!state.columns?.length) columns.value = defaultColumns(props.type)
  } else {
    table.applyState({ search: '', filters: {}, sort: [], columns: defaultColumns(props.type) })
  }
}

const currentState = computed(() => table.currentState())
const shownColumns = computed(() => visibleColumns(props.type, columns.value, context.value, quadrants.columns.value))
const allColumns = computed(() => columnsFor(props.type, context.value, quadrants.columns.value))
const statusOptions = computed(() =>
  info.hasStatus ? selectableStatuses(statuses.all.value, props.type, null).filter((s) => !s.isArchived) : [],
)
const quick: (keyof ItemFilters)[] = ['statusIds', 'assignedToMe', 'overdue', 'pinned', 'mine']
const furtherCount = computed(() => countFilters(filters.value, quick))
const activeCount = computed(() => countFilters(filters.value))
const filtersPanel = ref<InstanceType<typeof Popover> | null>(null)

function setFilter<K extends keyof ItemFilters>(key: K, value: ItemFilters[K]) {
  filters.value = { ...filters.value, [key]: value }
}

function clearFilters() {
  filters.value = {}
}

const conflicted = computed(() => new Set(sync.conflicts.filter((c) => c.table === 'items').map((c) => c.rowId)))
const canCreate = computed(() => session.allows(info.create))
const creating = ref(false)
// *New note* / *New idea* from a link (`?new=1`, the installed app's shortcuts).
const notify = useNotify()
watch(
  createRequested,
  (type) => {
    if (type !== props.type) return
    createRequested.value = null
    if (canCreate.value) creating.value = true
    else notify.info(`You can’t create ${info.plural.toLowerCase()}`, 'Your rights don’t include it.')
  },
  { immediate: true },
)
const selection = ref<Item[]>([])
const selectedIds = computed(() => selection.value.map((i) => i.id))

function excerpt(value: string | null): string {
  const line = (value ?? '')
    .split('\n')
    .map((l) => l.replace(/^[#>*\-\s]+/, '').trim())
    .find(Boolean)
  return line ? (line.length > 120 ? `${line.slice(0, 120)}…` : line) : ''
}

const dueOver = (item: Item) =>
  !!item.leadNextActionDue &&
  item.leadNextActionDue < today(now.value) &&
  statusOf(item, context.value).status?.category !== 'closed'

const open = (item: Item) => router.push(itemRoute(item))

/** A timebox date column's *overdue* marker (docs/01 §6.1): passed while the item isn't closed. */
function fieldOverdue(item: Item, field: FieldDefinitionRow): string | null {
  if (field.config?.timebox !== true) return null
  const closed = statusOf(item, context.value).status?.category === 'closed'
  return timeboxPassed(field, itemFieldValue(item, field, context.value), today(now.value), closed) ? 'Passed' : null
}
</script>

<template>
  <div class="page">
    <PageHeader :title="info.plural">
      <template #actions>
        <Button
          v-if="type === 'project'"
          label="Gate decisions"
          icon="pi pi-directions"
          size="small"
          severity="secondary"
          outlined
          @click="router.push({ name: 'gates' })"
        />
        <Button v-if="canCreate" :label="`New ${info.label}`" icon="pi pi-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <FilterBar
      v-model:search="table.search.value"
      :placeholder="`Search ${info.plural.toLowerCase()}…`"
      :active="activeCount"
    >
      <MultiSelect
        v-if="info.hasStatus"
        :model-value="filters.statusIds ?? []"
        :options="statusOptions"
        option-label="name"
        option-value="id"
        placeholder="Any status"
        :max-selected-labels="2"
        size="small"
        aria-label="Status"
        class="status-filter"
        @update:model-value="(v: string[]) => setFilter('statusIds', v)"
      >
        <template #option="{ option }">
          <span class="dot" :style="{ background: option.color }" aria-hidden="true" />{{ option.name }}
        </template>
      </MultiSelect>
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
        v-if="info.hasStatus"
        :model-value="!!filters.overdue"
        on-label="Overdue"
        off-label="Overdue"
        on-icon="pi pi-clock"
        off-icon="pi pi-clock"
        size="small"
        @update:model-value="(v: boolean) => setFilter('overdue', v || undefined)"
      />
      <ToggleButton
        v-if="type === 'note'"
        :model-value="!!filters.pinned"
        on-label="Pinned"
        off-label="Pinned"
        on-icon="pi pi-thumbtack"
        off-icon="pi pi-thumbtack"
        size="small"
        @update:model-value="(v: boolean) => setFilter('pinned', v || undefined)"
      />
      <ToggleButton
        :model-value="!!filters.mine"
        on-label="Mine"
        off-label="Mine"
        size="small"
        @update:model-value="(v: boolean) => setFilter('mine', v || undefined)"
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
        <ItemFiltersPanel v-model="filters" :type="type" />
        <QuadrantFilter
          v-if="quadrants.maps.value.length"
          :model-value="filters.quadrant"
          :maps="quadrants.maps.value"
          @update:model-value="(v) => setFilter('quadrant', v)"
        />
      </Popover>
      <Button v-if="activeCount" label="Clear" icon="pi pi-filter-slash" size="small" text @click="clearFilters" />
      <template #end>
        <SavedViewMenu v-model="viewId" :type="type" :plural="info.plural" :state="currentState" @apply="applyView" />
        <ColumnChooser v-model="columns" :columns="allColumns" :defaults="defaultColumns(type)" />
      </template>
    </FilterBar>
    <BulkActions v-if="selection.length" :type="type" :ids="selectedIds" @done="selection = []" />
    <ListTable
      v-model:selection="selection"
      :table="table"
      :what="info.plural.toLowerCase()"
      selectable
      fixed
      row-links
      @select="open"
    >
      <template v-if="sync.firstSync" #empty><FirstSyncNotice /></template>
      <Column selection-mode="multiple" header-style="width: 2.75rem" />
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
          <span v-if="col.id === 'key'" class="mono muted">{{ itemKey(data) }}</span>
          <template v-else-if="col.id === 'title'">
            <div class="title-cell">
              <i v-if="data.pinned" role="img" class="pi pi-thumbtack pin" aria-label="Pinned" />
              <RouterLink :to="itemRoute(data)" class="strong ellipsis" @click.stop>{{ data.title }}</RouterLink>
              <i
                v-if="conflicted.has(data.id)"
                v-tooltip.top="'Sync conflict'"
                role="img"
                class="pi pi-exclamation-triangle conflict"
                aria-label="Sync conflict"
              />
              <i
                v-else-if="data._local?.pending"
                v-tooltip.top="'Not synced yet'"
                role="img"
                class="pi pi-cloud-upload muted"
                aria-label="Not synced yet"
              />
            </div>
            <div v-if="type === 'note' && excerpt(data.description)" class="muted small ellipsis">
              {{ excerpt(data.description) }}
            </div>
          </template>
          <StatusBadge
            v-else-if="col.id === 'status'"
            :status="statusOf(data, context).status"
            :overdue="statusOf(data, context).overdue"
          />
          <span v-else-if="col.id === 'owner'" class="ellipsis">{{ users.nameOf(data.ownerUserId, '–') }}</span>
          <span v-else-if="col.id === 'persons'" class="ellipsis">{{ personNames(data, context).join(', ') }}</span>
          <span
            v-else-if="col.id === 'color'"
            role="img"
            class="swatch"
            :style="{ background: data.color ?? 'transparent' }"
            :aria-label="data.color ?? 'No colour'"
          />
          <RelativeTime v-else-if="col.id === 'created_at'" :value="data.createdAt" />
          <RelativeTime v-else-if="col.id === 'updated_at'" :value="data.updatedAt" />
          <i
            v-else-if="col.id === 'pinned'"
            :class="data.pinned ? 'pi pi-thumbtack pin' : ''"
            :role="data.pinned ? 'img' : undefined"
            :aria-label="data.pinned ? 'Pinned' : undefined"
          />
          <span v-else-if="col.id === 'links'" class="ellipsis" :title="linkSummary(data, context).text">
            <template v-if="linkSummary(data, context).count">
              {{ linkSummary(data, context).count }} · {{ linkSummary(data, context).text }}
            </template>
          </span>
          <span v-else-if="col.id === 'days_in_status'">{{ sortValue(data, 'days_in_status', context) ?? '' }}</span>
          <span v-else-if="col.id === 'value'">{{ formatValue(data.leadValue, data.leadCurrency) }}</span>
          <span v-else-if="col.id === 'probability'">{{
            data.leadProbability === null || data.leadProbability === undefined ? '' : `${data.leadProbability} %`
          }}</span>
          <span v-else-if="col.id === 'next_action_due'" :class="{ late: dueOver(data) }">{{
            formatDay(data.leadNextActionDue)
          }}</span>
          <span v-else-if="['decision_date', 'start_date', 'end_date'].includes(col.id)">{{
            formatDay(sortValue(data, col.id, context) as string | null)
          }}</span>
          <FieldValue
            v-else-if="col.field"
            :field="col.field"
            :value="itemFieldValue(data, col.field, context)"
            :context="renderContext"
            :overdue="fieldOverdue(data, col.field)"
            compact
          />
          <span v-else class="ellipsis">{{ sortValue(data, col.id, context) ?? '' }}</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="itemRoute(item)" class="mobile-row">
          <span class="strong ellipsis">
            <i v-if="item.pinned" role="img" class="pi pi-thumbtack pin" aria-label="Pinned" />
            {{ item.title }}
          </span>
          <span class="muted small">
            {{ itemKey(item) }}
            <template v-if="statusOf(item, context).status"> · {{ statusOf(item, context).status!.name }}</template>
            <template v-if="statusOf(item, context).overdue"> · overdue</template>
            · <RelativeTime :value="item.updatedAt" />
          </span>
        </RouterLink>
      </template>
    </ListTable>
    <ItemCreateDialog v-model:visible="creating" :type="type" />
  </div>
</template>

<style scoped>
.title-cell {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.pin {
  color: var(--p-primary-color);
  font-size: 0.857rem;
}

.conflict {
  color: var(--pgpm-warn);
}

.small {
  font-size: 0.857rem;
}

.swatch {
  display: inline-block;
  width: 0.875rem;
  height: 0.875rem;
  border-radius: 50%;
  border: 1px solid var(--pgpm-border);
  vertical-align: middle;
}

.dot {
  display: inline-block;
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
  margin-right: var(--pgpm-space-2);
}

.status-filter {
  min-width: 10rem;
}

.late {
  color: var(--pgpm-fail);
  font-weight: 600;
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
