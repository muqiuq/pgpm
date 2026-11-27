<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import { useQueryClient } from '@tanstack/vue-query'
import { trashApi, type TrashEntry } from '@/api/sync'
import EmptyState from '@/components/EmptyState.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { engine } from '@/local/boot'
import { useOnline } from '@/local/lookups'
import { useSession } from '@/stores/session'

/**
 * *Work → Trash* (docs/01 §4.9, docs/03 §11.6): the deleted items (and, from phase 5, maps) the user may manage – admins
 * all – with who deleted them and when. **Restore** brings back everything deleted with it, everywhere (the server
 * un-deletes the batch; the rows arrive through the sync). Admins **delete permanently**. Online only.
 */
const session = useSession()
const online = useOnline()
const queryClient = useQueryClient()
const { done, ask } = useActions()

/** A trash entry as a table row: keyed by its batch. */
type TrashRow = TrashEntry & { id: string }

const table = useServerTable<TrashRow>({
  key: ['trash'],
  fetch: async (q) => {
    const page = await trashApi.list(q)
    return { ...page, items: page.items.map((e) => ({ ...e, id: e.batchId })) }
  },
  defaultSort: '-deletedAt',
  syncUrl: true,
  enabled: online,
})

const icons: Record<string, string> = {
  note: 'pi pi-file-edit',
  idea: 'pi pi-lightbulb',
  lead: 'pi pi-flag',
  project: 'pi pi-briefcase',
  map: 'pi pi-map',
}
const iconOf = (entry: TrashEntry) => icons[entry.type ?? entry.rootKind] ?? 'pi pi-file'

async function restore(entry: TrashEntry) {
  const ok = await done(() => trashApi.restore(entry.batchId), {
    success: `${entry.title || 'It'} is restored`,
    failure: 'Not restored',
    invalidate: [['trash']],
  })
  if (ok && !engine.isLive) void engine.syncNow()
}

async function purge(entry: TrashEntry) {
  if (
    !(await ask(
      `Delete ${entry.title || 'this'} permanently?`,
      'It and everything deleted with it are removed for good, with their files. This cannot be undone.',
      'Delete permanently',
    ))
  )
    return
  await done(() => trashApi.purge(entry.batchId), {
    success: 'Deleted permanently',
    failure: 'Not deleted',
    invalidate: [['trash']],
  })
}

const refresh = () => queryClient.invalidateQueries({ queryKey: ['trash'] })
</script>

<template>
  <div class="page">
    <PageHeader title="Trash">
      <template #actions>
        <Button
          v-if="online"
          icon="pi pi-refresh"
          size="small"
          severity="secondary"
          text
          aria-label="Refresh"
          @click="refresh"
        />
      </template>
    </PageHeader>
    <EmptyState
      v-if="!online"
      icon="pi pi-wifi"
      title="Available online only"
      text="The Trash is kept on the server; restoring and deleting need a connection."
    />
    <template v-else>
      <FilterBar v-model:search="table.search.value" placeholder="Search the trash…" />
      <ListTable :table="table" what="the trash" empty-text="The trash is empty.">
        <Column field="title" header="Title" sortable>
          <template #body="{ data }">
            <span class="title-cell">
              <i :class="iconOf(data)" aria-hidden="true" />
              <span class="ellipsis">{{ data.title || 'Untitled' }}</span>
            </span>
          </template>
        </Column>
        <Column field="deletedAt" header="Deleted" sortable style="width: 10rem">
          <template #body="{ data }"><RelativeTime :value="data.deletedAt" /></template>
        </Column>
        <Column header="By" style="width: 12rem">
          <template #body="{ data }">{{ data.deletedByName ?? '–' }}</template>
        </Column>
        <Column header="" style="width: 16rem">
          <template #body="{ data }">
            <div class="row-actions">
              <Button
                label="Restore"
                icon="pi pi-replay"
                size="small"
                severity="secondary"
                outlined
                @click="restore(data)"
              />
              <Button
                v-if="session.isAdmin"
                v-tooltip.top="'Delete permanently'"
                icon="pi pi-times"
                size="small"
                severity="danger"
                text
                aria-label="Delete permanently"
                @click="purge(data)"
              />
            </div>
          </template>
        </Column>
        <template #mobile="{ item }">
          <div class="mobile-row">
            <span class="title-cell">
              <i :class="iconOf(item)" aria-hidden="true" />
              <span class="ellipsis">{{ item.title || 'Untitled' }}</span>
            </span>
            <span class="muted small"><RelativeTime :value="item.deletedAt" /> · {{ item.deletedByName ?? '–' }}</span>
            <div class="row-actions">
              <Button label="Restore" size="small" severity="secondary" outlined @click="restore(item)" />
              <Button
                v-if="session.isAdmin"
                label="Delete permanently"
                size="small"
                severity="danger"
                text
                @click="purge(item)"
              />
            </div>
          </div>
        </template>
      </ListTable>
    </template>
  </div>
</template>

<style scoped>
.title-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.row-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--pgpm-space-1);
}

.mobile-row {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
