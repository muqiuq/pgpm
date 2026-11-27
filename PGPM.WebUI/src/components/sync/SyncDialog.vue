<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import EmptyState from '@/components/EmptyState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { titleOf } from '@/local/apply'
import { localDb } from '@/local/db'
import { fieldLabel } from '@/local/lookups'
import type { OutboxEntry } from '@/local/outbox'
import { useLive } from '@/local/useLive'
import { useSyncStore } from '@/stores/sync'
import ConflictResolver from './ConflictResolver.vue'

/**
 * The sync details (docs/03 §12.6, docs/01 §14.3): the state of the live channel, the cursor, the last sync, the
 * changes waiting to go out, *Sync now*, and the conflicts.
 */
const sync = useSyncStore()

const pending = useLive(
  async () => {
    const db = localDb()
    const entries = await db.outbox.orderBy('seq').limit(100).toArray()
    return Promise.all(
      entries.map(async (entry) => ({ entry, title: titleOf(await db.synced(entry.table).get(entry.id)) })),
    )
  },
  [] as { entry: OutboxEntry; title: string | null }[],
)

const tabs = computed(() => [
  { label: 'Status', value: 'status' },
  { label: sync.conflictCount ? `Conflicts (${sync.conflictCount})` : 'Conflicts', value: 'conflicts' },
])

const stateText = computed(
  () =>
    ({
      live: 'Live: changes of others arrive within a second; yours go out right away.',
      syncing: 'Catching up with the server.',
      online: 'Connected, without the live channel: changes are fetched every 30 seconds.',
      offline: 'Offline: your changes are kept on this device and sent when the connection is back.',
      starting: 'Connecting…',
      'signed-out': 'Signed out: sign in again to send your changes.',
      stopped: 'Not syncing.',
    })[sync.state],
)

const tables: Record<string, string> = {
  items: 'Item',
  item_shares: 'Sharing',
  persons: 'Person',
  user_settings: 'Setting',
}
const opText = { insert: 'New', patch: 'Changed', delete: 'Deleted' }

function what(entry: OutboxEntry): string {
  if (entry.op !== 'patch' || !entry.changes) return ''
  return Object.keys(entry.changes)
    .map((k) => fieldLabel(entry.table, k))
    .join(', ')
}
</script>

<template>
  <Dialog
    v-model:visible="sync.dialogOpen"
    header="Synchronisation"
    modal
    :style="{ width: 'min(46rem, 100vw)' }"
    data-testid="sync-dialog"
  >
    <SelectButton
      v-model="sync.dialogTab"
      :options="tabs"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      size="small"
      class="tabs"
    />
    <div v-if="sync.dialogTab === 'status'" class="status">
      <Message v-if="sync.status.error && sync.state !== 'live'" severity="warn" size="small" :closable="false">
        {{ sync.status.error }}
      </Message>
      <p class="muted">{{ stateText }}</p>
      <dl class="facts">
        <dt>Last sync</dt>
        <dd><RelativeTime :value="sync.status.lastSyncAt" /></dd>
        <dt>Cursor</dt>
        <dd class="mono">{{ sync.status.cursor }}</dd>
        <dt>This tab</dt>
        <dd>{{ sync.status.leader ? 'Runs the sync' : 'Another tab of PGPM runs the sync' }}</dd>
        <dt>Pending</dt>
        <dd>{{ sync.pendingCount === 1 ? '1 change' : `${sync.pendingCount} changes` }}</dd>
      </dl>
      <EmptyState v-if="!pending.value.value.length" icon="pi pi-check" title="Everything is sent." />
      <ul v-else class="pending">
        <li v-for="{ entry, title } in pending.value.value" :key="entry.seq">
          <i
            :class="entry.sentAt ? 'pi pi-spin pi-spinner' : 'pi pi-clock'"
            :title="entry.sentAt ? 'Being sent' : 'Waiting'"
            aria-hidden="true"
          />
          <span class="op">{{ opText[entry.op] }}</span>
          <span class="muted">{{ tables[entry.table] ?? entry.table }}</span>
          <span class="ellipsis">{{ title ?? '' }}</span>
          <span v-if="what(entry)" class="muted ellipsis">{{ what(entry) }}</span>
          <RelativeTime class="when muted" :value="entry.clientChangedAt" />
        </li>
      </ul>
    </div>
    <ConflictResolver v-else :conflicts="sync.conflicts" @navigate="sync.dialogOpen = false" />
    <template #footer>
      <Button label="Close" severity="secondary" text @click="sync.dialogOpen = false" />
      <Button
        label="Sync now"
        icon="pi pi-sync"
        :loading="sync.status.syncing"
        :disabled="sync.state === 'signed-out'"
        @click="sync.syncNow()"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.tabs {
  margin-bottom: var(--pgpm-space-3);
}

.status {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.status p {
  margin: 0;
}

.pending {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 16rem;
  overflow-y: auto;
}

.pending li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-1) 0;
  border-top: 1px solid var(--pgpm-border);
  min-width: 0;
}

.op {
  font-weight: 500;
}

.when {
  margin-left: auto;
}
</style>
