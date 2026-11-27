import { watch } from 'vue'
import { liveUrl, postSync, trashApi } from '@/api/sync'
import { requestBackgroundSync } from '@/pwa/register'
import { useSession } from '@/stores/session'
import { claimLocalData, localDb, onWipe } from './db'
import { appDrafts } from './drafts'
import { clearEditing, receiveEditing } from './presence'
import { repo, setRepoContext } from './repo'
import { conflictActions } from './resolve'
import type { SocketLike } from './live'
import { SyncEngine, type Notice } from './sync'
import { tableDef } from './tables'

/**
 * The app's sync, wired to the session (docs/03 §11.7, docs/01 §14.5): it runs while a user is signed in and no gate
 * holds the session up; it stops on sign-out, and a sign-out wipes the local data first (session.ts). A different
 * user's sign-in wipes the previous user's data (claimLocalData). A session that ends meanwhile (401, close 4401) keeps
 * the data: the same user signs in again and the pending changes go out.
 */

const noticeListeners = new Set<(notice: Notice) => void>()

/** The engine's toasts (App.vue shows them). */
export function onSyncNotice(listener: (notice: Notice) => void): () => void {
  noticeListeners.add(listener)
  return () => noticeListeners.delete(listener)
}

let appVersion = 'unknown'

export const engine = new SyncEngine({
  post: postSync,
  socket: typeof WebSocket === 'undefined' ? null : (url) => new WebSocket(url) as unknown as SocketLike,
  liveUrl,
  appVersion: () => appVersion,
  backgroundSync: requestBackgroundSync,
  notify: (notice) => noticeListeners.forEach((l) => l(notice)),
  onEditing: receiveEditing,
  onSignedOut: () => void useSession().refresh(),
  onSession: (state) => void (state === 'rights_changed' ? useSession().load(true) : useSession().refresh()),
  onAppVersion: (version) => {
    if (version !== appVersion) {
      void navigator.serviceWorker
        ?.getRegistration()
        .then((r) => r?.update())
        .catch(() => undefined)
    }
  },
})

setRepoContext({ changed: () => engine.localChange() })
onWipe('before', () => engine.stop())
onWipe('after', () => clearEditing())

/** The conflict dialog's decisions on the app's data. */
export const conflicts = conflictActions({
  db: localDb,
  repo,
  restoreBatch: (batchId) => trashApi.restore(batchId),
  findBatch: async (_table, rowId) => {
    for (let page = 1; page <= 20; page++) {
      const found = await trashApi.list({ page, pageSize: 200 })
      const hit = found.items.find((t) => t.rootId === rowId)
      if (hit) return hit.batchId
      if (page * found.pageSize >= found.totalCount) break
    }
    return null
  },
  syncNow: () => engine.syncNow(),
})

let installed = false

/** Starts and stops the sync with the session; the drafts' mirror and sweep. Once, from main.ts. */
export function installSync() {
  if (installed) return
  installed = true
  const session = useSession()
  appDrafts.install()
  void appDrafts.promote()
  watch(
    () => session.info?.version,
    (version) => (appVersion = version ?? 'unknown'),
    { immediate: true },
  )
  watch(
    () => {
      const user = session.user
      const gated =
        !!session.flags?.mustChangePassword || !!session.flags?.totpSetupRequired || !!session.times?.totpDue
      return user && !gated ? user.id : null
    },
    async (userId) => {
      if (!userId) {
        await engine.stop()
        return
      }
      setRepoContext({ userId })
      await claimLocalData(userId)
      engine.start()
      void appDrafts.sweep(async (table, id) => !!tableDef(table) && !!(await localDb().synced(table).get(id)))
    },
    { immediate: true },
  )
}
