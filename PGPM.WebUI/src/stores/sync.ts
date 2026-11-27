import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { engine } from '@/local/boot'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb, onWipe } from '@/local/db'
import { useLive } from '@/local/useLive'

/**
 * The sync's state for the UI (docs/03 §12.6): the engine's status (live / syncing / offline, cursor, last sync, the last
 * error; in a follower tab as the leader broadcasts it) and, from IndexedDB in every tab, the pending changes and the
 * conflicts. The sync dialog opens from here.
 */
export type SyncState = 'live' | 'syncing' | 'online' | 'offline' | 'starting' | 'signed-out' | 'stopped'

export const useSyncStore = defineStore('sync', () => {
  const status = engine.status
  const pending = useLive(() => localDb().outbox.count(), 0)
  const conflictList = useLive(
    async () => (await localDb().conflicts.toArray()).sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
    [] as ConflictEntry[],
  )
  onWipe('after', () => {
    pending.refresh()
    conflictList.refresh()
  })

  const state = computed<SyncState>(() => {
    if (status.syncing) return 'syncing'
    switch (status.connection) {
      case 'live':
        return 'live'
      case 'online':
        return 'online'
      case 'offline':
        return 'offline'
      case 'signed-out':
        return 'signed-out'
      case 'stopped':
        return 'stopped'
      default:
        return 'starting'
    }
  })

  const dialogOpen = ref(false)
  const dialogTab = ref<'status' | 'conflicts'>('status')

  function openDialog(tab: 'status' | 'conflicts' = 'status') {
    dialogTab.value = tab
    dialogOpen.value = true
  }

  return {
    status,
    state,
    pendingCount: computed(() => pending.value.value),
    conflicts: computed(() => conflictList.value.value),
    conflictCount: computed(() => conflictList.value.value.length),
    dialogOpen,
    dialogTab,
    openDialog,
    syncNow: () => engine.syncNow(),
  }
})
