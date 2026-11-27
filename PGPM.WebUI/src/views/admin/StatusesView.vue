<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { statusesApi, type StatusInput } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { localDb } from '@/local/db'
import { useOnline } from '@/local/lookups'
import type { StatusCategory, StatusRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import StatusDialog from './StatusDialog.vue'

/**
 * *Administration → Statuses* (docs/01 §5, *Configuration: Manage*): one list per item type in its order – colour, icon,
 * name, category, default, max. days, archived. New, edit, move up/down, set default, archive/unarchive go through the
 * statuses endpoints (online only); the statuses are synced read-only rows, so the change appears on every device
 * through the sync (a *Sync now* follows each call, so this page shows it at once).
 */
type ItemType = StatusInput['itemType']

const route = useRoute()
const router = useRouter()
const notify = useNotify()
const online = useOnline()

/** The statuses and the assignment roles (phase 4) share the page's tabs. */
const tabs = [
  { key: 'statuses', label: 'Statuses', to: { name: 'statuses' } },
  { key: 'roles', label: 'Assignment roles', to: { name: 'roles' } },
]

const types: { label: string; value: ItemType }[] = [
  { label: 'Ideas', value: 'idea' },
  { label: 'Leads', value: 'lead' },
  { label: 'Projects', value: 'project' },
]
const initial = types.find((t) => t.value === route.query.type)?.value ?? 'idea'
const itemType = ref<ItemType>(initial)
watch(itemType, (type) => void router.replace({ query: { ...route.query, type: type === 'idea' ? undefined : type } }))
const showArchived = ref(false)

const all = useLive(
  async () =>
    ((await localDb().synced('statuses').where('itemType').equals(itemType.value).toArray()) as unknown as StatusRow[])
      .filter((s) => !s.deleted)
      .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)),
  [] as StatusRow[],
  () => itemType.value,
)
const shown = computed(() => all.value.value.filter((s) => showArchived.value || !s.isArchived))
const archivedCount = computed(() => all.value.value.filter((s) => s.isArchived).length)

const categoryTags: Record<StatusCategory, { label: string; severity: 'secondary' | 'info' | 'contrast' }> = {
  open: { label: 'Open', severity: 'secondary' },
  active: { label: 'Active', severity: 'info' },
  closed: { label: 'Closed', severity: 'contrast' },
}

const busy = ref(false)
async function call(action: () => Promise<unknown>, success: string, failure: string) {
  busy.value = true
  try {
    await action()
    notify.success(success)
    void engine.syncNow()
  } catch (e) {
    notify.error(e, failure)
  } finally {
    busy.value = false
  }
}

/** Swaps a status with its neighbour in the shown list; the order of the whole type goes to the server. */
function move(status: StatusRow, step: -1 | 1) {
  const list = shown.value
  const at = list.findIndex((s) => s.id === status.id)
  const other = list[at + step]
  if (!other) return
  const ids = all.value.value.map((s) => s.id)
  const a = ids.indexOf(status.id)
  const b = ids.indexOf(other.id)
  ids[a] = other.id
  ids[b] = status.id
  void call(() => statusesApi.order(itemType.value, ids), 'Order saved', 'Order not saved')
}

const update = (s: StatusRow, change: Partial<Pick<StatusRow, 'isDefault'>>) =>
  statusesApi.update(s.id, {
    itemType: s.itemType,
    name: s.name,
    color: s.color,
    icon: s.icon,
    category: s.category,
    isDefault: s.isDefault,
    maxDays: s.maxDays,
    ...change,
  })

function menu(s: StatusRow): MenuItem[] {
  const disabled = !online.value || busy.value
  return [
    {
      label: 'Set as default',
      icon: 'pi pi-star',
      disabled: disabled || s.isDefault || s.isArchived,
      command: () => void call(() => update(s, { isDefault: true }), `${s.name} is the default now`, 'Not changed'),
    },
    s.isArchived
      ? {
          label: 'Unarchive',
          icon: 'pi pi-replay',
          disabled,
          command: () => void call(() => statusesApi.unarchive(s.id), `${s.name} is selectable again`, 'Not changed'),
        }
      : {
          label: 'Archive',
          icon: 'pi pi-folder',
          disabled: disabled || s.isDefault,
          command: () => void call(() => statusesApi.archive(s.id), `${s.name} is archived`, 'Not archived'),
        },
  ]
}

const editing = ref(false)
const edited = ref<StatusRow | null>(null)
function openDialog(status: StatusRow | null) {
  edited.value = status
  editing.value = true
}
</script>

<template>
  <div class="page">
    <PageHeader title="Statuses" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button label="New status" icon="pi pi-plus" size="small" :disabled="!online" @click="openDialog(null)" />
      </template>
    </PageHeader>
    <TabNav :items="tabs" active="statuses" label="Statuses and roles" />
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: statuses can be changed with a connection.
    </Message>
    <div class="toolbar">
      <SelectButton
        v-model="itemType"
        :options="types"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Item type"
      />
      <span v-if="archivedCount" class="switch">
        <ToggleSwitch v-model="showArchived" input-id="st-archived" />
        <label for="st-archived">Show archived ({{ archivedCount }})</label>
      </span>
    </div>
    <section class="panel" aria-label="Statuses">
      <EmptyState v-if="all.loaded.value && !shown.length" icon="pi pi-tag" title="No statuses yet." />
      <ol v-else class="statuses">
        <li v-for="(s, i) in shown" :key="s.id" :class="{ archived: s.isArchived }" :data-testid="`status-${s.id}`">
          <span class="order">
            <Button
              icon="pi pi-angle-up"
              text
              size="small"
              severity="secondary"
              :disabled="!online || busy || i === 0"
              :aria-label="`Move ${s.name} up`"
              @click="move(s, -1)"
            />
            <Button
              icon="pi pi-angle-down"
              text
              size="small"
              severity="secondary"
              :disabled="!online || busy || i === shown.length - 1"
              :aria-label="`Move ${s.name} down`"
              @click="move(s, 1)"
            />
          </span>
          <span class="dot" :style="{ background: s.color }" aria-hidden="true" />
          <i :class="s.icon ? `pi ${s.icon}` : 'pi pi-circle'" class="icon" aria-hidden="true" />
          <span class="name strong ellipsis">{{ s.name }}</span>
          <Tag :value="categoryTags[s.category].label" :severity="categoryTags[s.category].severity" />
          <Tag v-if="s.isDefault" icon="pi pi-star" value="Default" severity="success" />
          <Tag v-if="s.isArchived" value="Archived" severity="secondary" />
          <span class="days muted">{{ s.maxDays ? `max. ${s.maxDays} days` : '' }}</span>
          <span class="actions">
            <Button
              icon="pi pi-pencil"
              text
              size="small"
              severity="secondary"
              :disabled="!online"
              :aria-label="`Edit ${s.name}`"
              @click="openDialog(s)"
            />
            <MoreMenu :items="menu(s)" :label="`More actions for ${s.name}`" />
          </span>
        </li>
      </ol>
    </section>
    <StatusDialog
      v-model:visible="editing"
      :item-type="itemType"
      :status="edited"
      :disabled="!online"
      @saved="engine.syncNow()"
    />
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-4);
  flex-wrap: wrap;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.statuses {
  list-style: none;
  margin: 0;
  padding: 0;
}

.statuses li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 40px;
  padding: 0 var(--pgpm-space-3) 0 var(--pgpm-space-1);
  border-bottom: 1px solid var(--pgpm-border);
}

.statuses li:last-child {
  border-bottom: none;
}

.statuses li.archived {
  opacity: 0.6;
}

.order {
  display: inline-flex;
}

.dot {
  width: 12px;
  height: 12px;
  border-radius: 50%;
  flex: none;
}

.icon {
  width: 16px;
  text-align: center;
  color: var(--pgpm-muted);
}

.name {
  min-width: 6rem;
}

.days {
  margin-left: auto;
  font-size: 0.857rem;
  white-space: nowrap;
}

.actions {
  display: inline-flex;
  align-items: center;
}
</style>
