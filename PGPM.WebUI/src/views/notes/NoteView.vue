<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import EmptyState from '@/components/EmptyState.vue'
import InlineField from '@/components/InlineField.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import ShareDialog from '@/components/share/ShareDialog.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useEditors } from '@/local/presence'
import { useGroups, useUserPersons } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey } from '@/local/rows'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import ItemHistory from './ItemHistory.vue'
import { useNote } from './useNote'

/**
 * A note's page (docs/01 §4.4, §7): the header line with key, title, *Share* and ⋯ (delete); the title edited inline,
 * the content rendered (Markdown) with *Edit* to the editing page; pinned; the History tab (online). Everything is
 * written to the local data and synced; with *read* access everything is read-only.
 */
const props = defineProps<{ id: string; tab?: string }>()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()
const { ask, notify } = useActions()
const { note, loaded, shares, conflicts, canEdit, canManage } = useNote(() => props.id)
const groups = useGroups()
const people = useUserPersons()
const editors = useEditors(() => props.id, {
  me: () => (session.user ? { id: session.user.id, name: session.user.displayName } : null),
})

const sharing = ref(false)
const activeTab = computed(() => (props.tab === 'history' ? 'history' : 'overview'))
const tabs = computed(() => [
  { key: 'overview', label: 'Overview', to: { name: 'note', params: { id: props.id } } },
  { key: 'history', label: 'History', to: { name: 'note', params: { id: props.id, tab: 'history' } } },
])

const key = computed(() => (note.value ? itemKey(note.value) : ''))
const shareText = computed(() => {
  if (!shares.value.length) return 'Private'
  const names = shares.value.map((s) => groups.value.find((g) => g.id === s.groupId)?.name ?? 'a group')
  return names.length <= 2 ? names.join(', ') : `${names.length} groups`
})
const shareList = computed(() =>
  shares.value.map((s) => ({
    name: groups.value.find((g) => g.id === s.groupId)?.name ?? 'Unknown group',
    access: s.access,
  })),
)

async function saveTitle(value: string | number | boolean | null) {
  const title = String(value ?? '').trim()
  if (!title || title.length > 300) {
    throw new ApiError(400, {
      code: 'validation_failed',
      errors: { title: [title ? 'At most 300 characters.' : 'A title is needed.'] },
    })
  }
  await repo.patch('items', props.id, { title })
}

async function togglePin() {
  if (!note.value) return
  try {
    await repo.patch('items', props.id, { pinned: !note.value.pinned })
  } catch (e) {
    notify.error(e, 'Not changed')
  }
}

async function remove() {
  if (!note.value) return
  if (!(await ask(`Delete ${note.value.title}?`, 'It goes to the Trash, from where it can be restored.', 'Delete')))
    return
  try {
    await repo.remove('items', props.id)
    notify.success('Note deleted', 'It is in the Trash.')
    await router.push({ name: 'notes' })
  } catch (e) {
    notify.error(e, 'Not deleted')
  }
}

const more = computed<MenuItem[]>(() =>
  canManage.value && !note.value?.deleted ? [{ label: 'Delete note…', icon: 'pi pi-trash', command: remove }] : [],
)
</script>

<template>
  <div class="page">
    <div v-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else-if="!note">
      <PageHeader title="Note" :crumbs="[{ label: 'Notes', to: { name: 'notes' } }]" />
      <EmptyState
        icon="pi pi-eye-slash"
        title="This note is not on this device."
        text="It was deleted, is no longer shared with you, or has not arrived yet."
      >
        <Button label="All notes" size="small" severity="secondary" outlined @click="router.push({ name: 'notes' })" />
      </EmptyState>
    </template>
    <template v-else>
      <PageHeader :title="note.title" :crumbs="[{ label: 'Notes', to: { name: 'notes' } }]">
        <template #status>
          <span class="mono muted">{{ key }}</span>
          <Tag v-if="note.pinned" icon="pi pi-thumbtack" value="Pinned" severity="secondary" />
          <Tag v-if="note.access === 'read'" value="Read only" severity="secondary" />
          <Tag
            v-if="conflicts.length"
            v-tooltip.bottom="'Open the conflict'"
            icon="pi pi-exclamation-triangle"
            value="Conflict"
            severity="warn"
            class="clickable"
            @click="sync.openDialog('conflicts')"
          />
          <Tag v-else-if="note._local?.pending" icon="pi pi-cloud-upload" value="Not synced yet" severity="secondary" />
          <span v-for="e in editors" :key="`${e.userName}-${e.target}`" class="muted editing">
            <i class="pi pi-pencil" aria-hidden="true" /> {{ e.userName }} is editing
          </span>
        </template>
        <template #actions>
          <Button
            v-if="canEdit"
            v-tooltip.bottom="note.pinned ? 'Unpin' : 'Pin to the top of the list'"
            icon="pi pi-thumbtack"
            :severity="note.pinned ? 'primary' : 'secondary'"
            :outlined="!note.pinned"
            size="small"
            :aria-label="note.pinned ? 'Unpin' : 'Pin'"
            :aria-pressed="note.pinned"
            @click="togglePin"
          />
          <Button
            :label="`Share · ${shareText}`"
            icon="pi pi-users"
            size="small"
            severity="secondary"
            outlined
            :disabled="note.deleted"
            @click="sharing = true"
          />
          <MoreMenu v-if="more.length" :items="more" label="More note actions" />
        </template>
      </PageHeader>
      <Message v-if="note.deleted" severity="warn" :closable="false">
        This note was deleted.
        <RouterLink v-if="canManage" :to="{ name: 'trash' }">Open the Trash</RouterLink>
      </Message>
      <TabNav :items="tabs" :active="activeTab" label="Note" />
      <div v-if="activeTab === 'overview'" class="two-columns">
        <section class="panel content" aria-labelledby="content-title">
          <div class="panel-header with-action">
            <span id="content-title">Content</span>
            <Button
              v-if="canEdit"
              label="Edit"
              icon="pi pi-pencil"
              size="small"
              text
              @click="router.push({ name: 'note-edit', params: { id } })"
            />
          </div>
          <div class="panel-body">
            <MarkdownView v-if="note.description" :source="note.description" />
            <EmptyState v-else icon="pi pi-align-left" title="No content yet." />
          </div>
        </section>
        <section class="panel" aria-labelledby="details-title">
          <div id="details-title" class="panel-header">Details</div>
          <div class="panel-body form">
            <InlineField :value="note.title" :save="saveTitle" field="title" label="Title" :readonly="!canEdit" />
            <dl class="facts">
              <dt>Key</dt>
              <dd class="mono">{{ note.number === null ? 'Assigned at the first sync' : key }}</dd>
              <dt>Owner</dt>
              <dd>{{ people.nameOf(note.ownerUserId, '–') }}</dd>
              <dt>Shared with</dt>
              <dd>
                <span v-if="!shareList.length" class="muted">Nobody (private)</span>
                <span v-for="s in shareList" :key="s.name" class="share"
                  >{{ s.name }} <small class="muted">{{ s.access }}</small></span
                >
              </dd>
              <dt>Created</dt>
              <dd><RelativeTime :value="note.createdAt" /> · {{ people.nameOf(note.createdBy, '–') }}</dd>
              <dt>Modified</dt>
              <dd><RelativeTime :value="note.updatedAt" /> · {{ people.nameOf(note.updatedBy, '–') }}</dd>
            </dl>
          </div>
        </section>
      </div>
      <ItemHistory v-else :item-id="id" type="note" />
      <ShareDialog
        v-model:visible="sharing"
        :item-id="id"
        :title="note.title"
        :owner-user-id="note.ownerUserId"
        :access="note.access"
      />
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.content .panel-body {
  min-height: 8rem;
}

.share {
  display: inline-block;
  margin-right: var(--pgpm-space-3);
}

.clickable {
  cursor: pointer;
}

.editing {
  font-size: 0.857rem;
}
</style>
