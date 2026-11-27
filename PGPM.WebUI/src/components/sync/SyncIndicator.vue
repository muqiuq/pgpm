<script setup lang="ts">
import { computed } from 'vue'
import { formatAbsolute } from '@/format'
import { useSyncStore } from '@/stores/sync'
import SyncDialog from './SyncDialog.vue'

/**
 * The sync indicator in the app bar (docs/03 §12.6, docs/01 §14.3): *live* (green dot), *syncing*, *online* (without
 * the live channel), *offline* (grey), the pending changes and the conflicts (orange). A click opens the sync dialog.
 */
const sync = useSyncStore()

const label = computed(
  () =>
    ({
      live: 'Live',
      syncing: 'Syncing',
      online: 'Online',
      offline: 'Offline',
      starting: 'Connecting',
      'signed-out': 'Signed out',
      stopped: 'Not syncing',
    })[sync.state],
)

const tooltip = computed(() => {
  const parts = [sync.status.lastSyncAt ? `Last sync ${formatAbsolute(sync.status.lastSyncAt)}` : 'Not synced yet']
  if (sync.pendingCount) parts.push(`${sync.pendingCount} pending`)
  if (sync.conflictCount) parts.push(`${sync.conflictCount} conflict${sync.conflictCount === 1 ? '' : 's'}`)
  return parts.join(' · ')
})

const ariaLabel = computed(() => {
  const parts = [label.value]
  if (sync.pendingCount) parts.push(`${sync.pendingCount} pending`)
  if (sync.conflictCount) parts.push(`${sync.conflictCount} conflicts`)
  return `Sync: ${parts.join(', ')}`
})
</script>

<template>
  <button
    v-tooltip.bottom="tooltip"
    type="button"
    class="sync-indicator"
    :class="[sync.state, { conflicts: sync.conflictCount > 0 }]"
    :aria-label="ariaLabel"
    aria-haspopup="dialog"
    data-testid="sync-indicator"
    @click="sync.openDialog(sync.conflictCount ? 'conflicts' : 'status')"
  >
    <span class="dot" aria-hidden="true" />
    <span class="label">{{ label }}</span>
    <span v-if="sync.pendingCount" class="count" data-testid="sync-pending">{{ sync.pendingCount }}</span>
    <span v-if="sync.conflictCount" class="count conflict" data-testid="sync-conflicts">
      <i class="pi pi-exclamation-triangle" aria-hidden="true" />{{ sync.conflictCount }}
    </span>
  </button>
  <SyncDialog />
</template>

<style scoped>
.sync-indicator {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: 0 var(--pgpm-space-2);
  height: 28px;
  border: none;
  border-radius: 999px;
  background: transparent;
  font: inherit;
  font-size: 0.857rem;
  color: var(--pgpm-muted);
  white-space: nowrap;
  cursor: pointer;
}

.sync-indicator:hover {
  background: var(--p-content-hover-background);
}

.sync-indicator:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--pgpm-none, var(--pgpm-muted));
}

.live .dot {
  background: var(--pgpm-ok);
}

.online .dot,
.starting .dot {
  background: var(--p-primary-color);
}

.syncing .dot {
  background: var(--p-primary-color);
  animation: pulse 1s ease-in-out infinite;
}

.offline,
.signed-out {
  background: var(--pgpm-none-soft, transparent);
}

.offline .dot,
.signed-out .dot,
.stopped .dot {
  background: var(--pgpm-muted);
}

.count {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  min-width: 1.25rem;
  justify-content: center;
  padding: 0 0.35rem;
  border-radius: 999px;
  background: var(--p-content-hover-background);
  color: var(--pgpm-text);
  font-variant-numeric: tabular-nums;
}

.count.conflict {
  background: var(--pgpm-warn-soft);
  color: var(--pgpm-warn);
}

.count i {
  font-size: 0.75rem;
}

@keyframes pulse {
  50% {
    opacity: 0.3;
  }
}

@media (max-width: 599px) {
  .label {
    display: none;
  }
}
</style>
