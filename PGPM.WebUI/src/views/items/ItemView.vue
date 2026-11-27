<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import ConvertDialog from '@/components/items/ConvertDialog.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import ShareDialog from '@/components/share/ShareDialog.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { itemRoute, itemTabs, itemTypes, type ItemTab } from '@/domain/itemTypes'
import { overdueReason } from '@/local/items'
import { useGroups, useStatuses } from '@/local/lookups'
import { useEditors } from '@/local/presence'
import { repo } from '@/local/repo'
import { itemKey, type ItemType } from '@/local/rows'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import ItemMapsTab from '@/views/maps/ItemMapsTab.vue'
import ItemHistory from './ItemHistory.vue'
import ItemLinks from './ItemLinks.vue'
import ItemOverview from './ItemOverview.vue'
import { useItem } from './useItem'

/**
 * An item's page (docs/01 §4.4): the header line with type icon, key, title, status (with *overdue*), *Share* and the
 * ⋯ menu (delete); the tabs Overview · Milestones (projects, phase 4) · Links · Maps · History, each with its
 * own route (`/<list>/:id/<tab>`). Ideas and leads offer *Convert to project*. Everything is written to the local data
 * and synced; with *read* access the page is read-only.
 */
const props = defineProps<{ type: ItemType; id: string; tab?: string }>()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()
const now = useNow()
const { ask, notify } = useActions()
const { item, loaded, shares, conflicts, converted, origin, canEdit, canManage } = useItem(
  () => props.type,
  () => props.id,
)
const groups = useGroups()
const statuses = useStatuses()
const editors = useEditors(() => props.id, {
  me: () => (session.user ? { id: session.user.id, name: session.user.displayName } : null),
})
const info = computed(() => itemTypes[props.type])

const sharing = ref(false)
const converting = ref(false)
const tabLabels: Record<ItemTab, string> = {
  overview: 'Overview',
  milestones: 'Milestones',
  links: 'Links',
  maps: 'Maps',
  history: 'History',
}
const activeTab = computed<ItemTab>(() => {
  const tabs = itemTabs(props.type)
  return tabs.includes(props.tab as ItemTab) ? (props.tab as ItemTab) : 'overview'
})
const tabs = computed(() =>
  itemTabs(props.type).map((key) => ({
    key,
    label: tabLabels[key],
    to: itemRoute({ type: props.type, id: props.id }, key === 'overview' ? undefined : key),
  })),
)

const key = computed(() => (item.value ? itemKey(item.value) : ''))
const status = computed(() => (item.value?.statusId ? statuses.byId.value.get(item.value.statusId) : undefined))
const overdue = computed(() => (item.value ? overdueReason(item.value, status.value, now.value) : null))
const shareText = computed(() => {
  if (!shares.value.length) return 'Private'
  const names = shares.value.map((s) => groups.value.find((g) => g.id === s.groupId)?.name ?? 'a group')
  return names.length <= 2 ? names.join(', ') : `${names.length} groups`
})
const crumbs = computed(() => [{ label: info.value.plural, to: { name: info.value.list } }])
const canConvert = computed(
  () =>
    info.value.convertible &&
    canEdit.value &&
    !converted.value &&
    session.allows(itemTypes.project.create) &&
    !item.value?.deleted,
)

async function togglePin() {
  if (!item.value) return
  try {
    await repo.patch('items', props.id, { pinned: !item.value.pinned })
  } catch (e) {
    notify.error(e, 'Not changed')
  }
}

async function remove() {
  if (!item.value) return
  if (!(await ask(`Delete ${item.value.title}?`, 'It goes to the Trash, from where it can be restored.', 'Delete')))
    return
  try {
    await repo.remove('items', props.id)
    notify.success(`${info.value.title} deleted`, 'It is in the Trash.')
    await router.push({ name: info.value.list })
  } catch (e) {
    notify.error(e, 'Not deleted')
  }
}

const more = computed<MenuItem[]>(() =>
  canManage.value && !item.value?.deleted
    ? [{ label: `Delete ${info.value.label}…`, icon: 'pi pi-trash', command: remove }]
    : [],
)
</script>

<template>
  <div class="page">
    <div v-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else-if="!item">
      <PageHeader :title="info.title" :crumbs="crumbs" />
      <EmptyState
        icon="pi pi-eye-slash"
        :title="`This ${info.label} is not on this device.`"
        text="It was deleted, is no longer shared with you, or has not arrived yet."
      >
        <Button
          :label="`All ${info.plural.toLowerCase()}`"
          size="small"
          severity="secondary"
          outlined
          @click="router.push({ name: info.list })"
        />
      </EmptyState>
    </template>
    <template v-else>
      <PageHeader :title="item.title" :crumbs="crumbs">
        <template #status>
          <i :class="info.icon" class="muted" aria-hidden="true" />
          <span class="mono muted">{{ key }}</span>
          <StatusBadge v-if="info.hasStatus" :status="status" :overdue="overdue" />
          <Tag v-if="item.pinned" icon="pi pi-thumbtack" value="Pinned" severity="secondary" />
          <RouterLink v-if="converted" :to="itemRoute(converted)" class="converted">
            <Tag icon="pi pi-arrow-right" :value="`Converted to ${itemKey(converted)}`" severity="success" />
          </RouterLink>
          <Tag v-if="item.access === 'read'" value="Read only" severity="secondary" />
          <Tag
            v-if="conflicts.length"
            v-tooltip.bottom="'Open the conflict'"
            icon="pi pi-exclamation-triangle"
            value="Conflict"
            severity="warn"
            class="clickable"
            @click="sync.openDialog('conflicts')"
          />
          <Tag v-else-if="item._local?.pending" icon="pi pi-cloud-upload" value="Not synced yet" severity="secondary" />
          <span v-for="e in editors" :key="`${e.userName}-${e.target}`" class="muted editing">
            <i class="pi pi-pencil" aria-hidden="true" /> {{ e.userName }} is editing
          </span>
        </template>
        <template #actions>
          <Button
            v-if="type === 'note' && canEdit"
            v-tooltip.bottom="item.pinned ? 'Unpin' : 'Pin to the top of the list'"
            icon="pi pi-thumbtack"
            :severity="item.pinned ? 'primary' : 'secondary'"
            :outlined="!item.pinned"
            size="small"
            :aria-label="item.pinned ? 'Unpin' : 'Pin'"
            :aria-pressed="item.pinned"
            @click="togglePin"
          />
          <Button
            v-if="canConvert"
            label="Convert to project"
            icon="pi pi-arrow-right"
            size="small"
            severity="secondary"
            outlined
            @click="converting = true"
          />
          <Button
            :label="`Share · ${shareText}`"
            icon="pi pi-users"
            size="small"
            severity="secondary"
            outlined
            :disabled="item.deleted"
            @click="sharing = true"
          />
          <MoreMenu v-if="more.length" :items="more" :label="`More ${info.label} actions`" />
        </template>
      </PageHeader>
      <Message v-if="item.deleted" severity="warn" :closable="false">
        This {{ info.label }} was deleted.
        <RouterLink v-if="canManage" :to="{ name: 'trash' }">Open the Trash</RouterLink>
      </Message>
      <TabNav :items="tabs" :active="activeTab" :label="info.title" />
      <ItemOverview
        v-if="activeTab === 'overview'"
        :item="item"
        :can-edit="canEdit"
        :shares="shares"
        :converted="converted"
        :origin="origin"
      />
      <ItemLinks v-else-if="activeTab === 'links'" :item="item" :can-edit="canEdit" />
      <EmptyState
        v-else-if="activeTab === 'milestones'"
        icon="pi pi-flag"
        title="Milestones and gates come with phase 4."
        text="Milestones, gates and their Definition of Done will be planned and decided here."
      />
      <ItemMapsTab v-else-if="activeTab === 'maps'" :item="item" />
      <ItemHistory v-else :item-id="id" :type="type" />
      <ShareDialog
        v-model:visible="sharing"
        :item-id="id"
        :title="item.title"
        :owner-user-id="item.ownerUserId"
        :access="item.access"
      />
      <ConvertDialog v-if="info.convertible" v-model:visible="converting" :source="item" />
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

.clickable {
  cursor: pointer;
}

.editing {
  font-size: 0.857rem;
}

.converted {
  text-decoration: none;
}
</style>
