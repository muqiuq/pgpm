<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import ToggleButton from 'primevue/togglebutton'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useLocalTable } from '@/composables/useLocalTable'
import { localDb } from '@/local/db'
import { useUserPersons } from '@/local/lookups'
import { itemKey, type ItemRow, type Stored } from '@/local/rows'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import NoteCreateDialog from './NoteCreateDialog.vue'

/**
 * *Work → Notes* (docs/01 §7, §4.3): every note on this device – searched, filtered and sorted in memory, offline; pinned
 * notes first. The list state (search, sort, filters) is remembered per user (`user_settings` `list:note`).
 */
type Note = Stored<ItemRow>

const route = useRoute()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()
const people = useUserPersons()

const pinnedOnly = ref(route.query.pinned === '1')
const mine = ref(route.query.mine === '1')
const filters = computed(() => ({ pinned: pinnedOnly.value || undefined, mine: mine.value || undefined }))
const urlQuery = computed(() => ({ pinned: pinnedOnly.value ? '1' : undefined, mine: mine.value ? '1' : undefined }))

const table = useLocalTable<Note>({
  rows: async () => (await localDb().synced('items').where('type').equals('note').toArray()) as unknown as Note[],
  text: (n) => `${itemKey(n)} ${n.title} ${n.description ?? ''}`,
  filters,
  match: (n, f) => !n.deleted && (!f.pinned || n.pinned) && (!f.mine || n.ownerUserId === session.user?.id),
  sortValue: (n, field) => (field === 'key' ? (n.number ?? Number.MAX_SAFE_INTEGER) : n[field as keyof Note]),
  lead: (a, b) => Number(!!b.pinned) - Number(!!a.pinned),
  defaultSort: '-updatedAt',
  syncUrl: true,
  urlQuery,
  settingsKey: 'list:note',
  remember: () => ({ pinned: pinnedOnly.value, mine: mine.value }),
  restore: (f) => {
    pinnedOnly.value = f.pinned === true
    mine.value = f.mine === true
  },
})

const conflicted = computed(() => new Set(sync.conflicts.filter((c) => c.table === 'items').map((c) => c.rowId)))
const canCreate = computed(() => session.allows(['Notes', 'Create']))
const creating = ref(false)
const active = computed(() => Number(pinnedOnly.value) + Number(mine.value))

function excerpt(text: string | null): string {
  const line = (text ?? '')
    .split('\n')
    .map((l) => l.replace(/^[#>*\-\s]+/, '').trim())
    .find(Boolean)
  return line ? (line.length > 120 ? `${line.slice(0, 120)}…` : line) : ''
}

const open = (note: Note) => router.push({ name: 'note', params: { id: note.id } })
</script>

<template>
  <div class="page">
    <PageHeader title="Notes">
      <template #actions>
        <Button v-if="canCreate" label="New note" icon="pi pi-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search notes…" :active="active">
      <ToggleButton
        v-model="pinnedOnly"
        on-label="Pinned"
        off-label="Pinned"
        on-icon="pi pi-thumbtack"
        off-icon="pi pi-thumbtack"
        size="small"
      />
      <ToggleButton v-model="mine" on-label="Mine" off-label="Mine" size="small" />
    </FilterBar>
    <ListTable :table="table" what="notes" selectable fixed @select="open">
      <Column field="key" header="Key" sortable style="width: 6.5rem">
        <template #body="{ data }">
          <span class="mono muted">{{ itemKey(data) }}</span>
        </template>
      </Column>
      <Column field="title" header="Title" sortable>
        <template #body="{ data }">
          <div class="title-cell">
            <i v-if="data.pinned" class="pi pi-thumbtack pin" aria-label="Pinned" />
            <RouterLink :to="{ name: 'note', params: { id: data.id } }" class="strong ellipsis" @click.stop>{{
              data.title
            }}</RouterLink>
            <i
              v-if="conflicted.has(data.id)"
              v-tooltip.top="'Sync conflict'"
              class="pi pi-exclamation-triangle conflict"
              aria-label="Sync conflict"
            />
            <i
              v-else-if="data._local?.pending"
              v-tooltip.top="'Not synced yet'"
              class="pi pi-cloud-upload muted"
              aria-label="Not synced yet"
            />
          </div>
          <div v-if="excerpt(data.description)" class="muted small ellipsis">{{ excerpt(data.description) }}</div>
        </template>
      </Column>
      <Column field="ownerUserId" header="Owner" style="width: 12rem">
        <template #body="{ data }">
          <span class="ellipsis">{{ people.nameOf(data.ownerUserId, '–') }}</span>
        </template>
      </Column>
      <Column field="updatedAt" header="Modified" sortable style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.updatedAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'note', params: { id: item.id } }" class="mobile-row">
          <span class="strong ellipsis">
            <i v-if="item.pinned" class="pi pi-thumbtack pin" aria-label="Pinned" />
            {{ item.title }}
          </span>
          <span class="muted small">{{ itemKey(item) }} · <RelativeTime :value="item.updatedAt" /></span>
        </RouterLink>
      </template>
    </ListTable>
    <NoteCreateDialog v-model:visible="creating" />
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

.mobile-row {
  display: flex;
  flex-direction: column;
  color: inherit;
  text-decoration: none;
}
</style>
