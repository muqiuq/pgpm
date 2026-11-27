<script setup lang="ts">
import Column from 'primevue/column'
import Tag from 'primevue/tag'

import { historyApi, type HistoryEntry } from '@/api/sync'
import EmptyState from '@/components/EmptyState.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { localDb } from '@/local/db'
import { fieldLabel, useOnline, useUserPersons } from '@/local/lookups'
import type { OutboxEntry } from '@/local/outbox'
import { useLive } from '@/local/useLive'

/**
 * An item's History tab (docs/01 §4.8): every change recorded on the server – who, when, from where, field by field;
 * online only (`GET /api/v1/history?itemId=`). Above it, the changes of this device not synced yet (offline that is
 * all there is).
 */
const props = defineProps<{ itemId: string; type?: string }>()
const online = useOnline()
const people = useUserPersons()

const table = useServerTable<HistoryEntry>({
  key: ['history', props.itemId],
  fetch: (q) => historyApi.list({ itemId: props.itemId, page: q.page, pageSize: q.pageSize }),
  pageSize: 25,
  enabled: online,
})

const local = useLive(
  () => localDb().outbox.where('[table+id]').equals(['items', props.itemId]).sortBy('seq'),
  [] as OutboxEntry[],
  () => props.itemId,
)

const actions: Record<string, string> = {
  create: 'Created',
  update: 'Changed',
  delete: 'Deleted',
  restore: 'Restored',
  share: 'Sharing',
  conflict: 'Conflict',
  conflict_resolved: 'Conflict resolved',
}

function short(value: unknown): string {
  if (value === null || value === undefined || value === '') return '–'
  const text = typeof value === 'string' ? value : JSON.stringify(value)
  return text.length > 80 ? `${text.slice(0, 80)}…` : text
}

function changes(entry: HistoryEntry): string[] {
  const list = Array.isArray(entry.changes) ? entry.changes : entry.changes ? [entry.changes] : []
  return list.map((c) => {
    const change = c as Record<string, unknown>
    const field = typeof change.field === 'string' ? fieldLabel(entry.entity ?? 'items', change.field, props.type) : ''
    if ('resolution' in change) return `${field}: kept ${String(change.resolution)}`
    if ('theirs' in change) return `${field}: ${short(change.yours)} / theirs ${short(change.theirs)}`
    return field ? `${field}: ${short(change.old)} → ${short(change.new)}` : short(change)
  })
}

function pendingText(entry: OutboxEntry): string {
  if (entry.op === 'insert') return 'Created on this device'
  if (entry.op === 'delete') return 'Deleted on this device'
  return Object.entries(entry.changes ?? {})
    .map(([k, c]) => `${fieldLabel('items', k, props.type)}: ${short(c.old)} → ${short(c.new)}`)
    .join(' · ')
}
</script>

<template>
  <div class="history">
    <section v-if="local.value.value.length" class="panel" aria-labelledby="pending-title">
      <div id="pending-title" class="panel-header">Not synced yet</div>
      <ul class="panel-body pending">
        <li v-for="entry in local.value.value" :key="entry.seq">
          <RelativeTime :value="entry.clientChangedAt" class="muted" />
          <span>{{ pendingText(entry) }}</span>
        </li>
      </ul>
    </section>
    <EmptyState
      v-if="!online"
      icon="pi pi-wifi"
      title="Available online only"
      text="The history is kept on the server; it shows once the connection is back."
    />
    <ListTable v-else :table="table" what="history" empty-text="No changes recorded yet.">
      <Column header="When" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Who" style="width: 12rem">
        <template #body="{ data }">
          <span class="ellipsis">{{ data.userName ?? people.nameOf(data.userId, 'PGPM') }}</span>
          <Tag v-if="data.source && data.source !== 'ui'" :value="data.source" severity="secondary" class="source" />
        </template>
      </Column>
      <Column header="What" style="width: 9rem">
        <template #body="{ data }">{{ actions[data.action] ?? data.action }}</template>
      </Column>
      <Column header="Changes">
        <template #body="{ data }">
          <div v-for="(line, i) in changes(data)" :key="i" class="change">{{ line }}</div>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="muted small">
          <RelativeTime :value="item.at" /> · {{ item.userName ?? people.nameOf(item.userId, 'PGPM') }} ·
          {{ actions[item.action] ?? item.action }}
        </div>
        <div v-for="(line, i) in changes(item)" :key="i" class="change">{{ line }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.history {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.pending {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.pending li {
  display: flex;
  gap: var(--pgpm-space-3);
}

.change {
  overflow-wrap: anywhere;
}

.source {
  margin-left: var(--pgpm-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
