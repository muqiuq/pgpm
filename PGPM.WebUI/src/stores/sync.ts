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
  // When this device last synced (meta `lastSyncAt`): never means the first sync after signing in is still running.
  const lastSynced = useLive(() => localDb().getMeta<string | null>('lastSyncAt', null), null as string | null)
  onWipe('after', () => {
    pending.refresh()
    conflictList.refresh()
    lastSynced.refresh()
  })
  /**
   * The first sync after signing in hasn't finished (docs/03 §12.6): the statuses, templates, sections and groups may
   * not be on the device yet, so creating waits and the lists say why they are empty. Only while the engine runs (a
   * stopped one – signed out, the tests – never syncs).
   */
  const firstSync = computed(
    () =>
      lastSynced.loaded.value &&
      !lastSynced.value.value &&
      !status.lastSyncAt &&
      status.connection !== 'stopped' &&
      status.connection !== 'signed-out',
  )

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
    firstSync,
    dialogOpen,
    dialogTab,
    openDialog,
    syncNow: () => engine.syncNow(),
  }
})
