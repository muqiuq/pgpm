import { defineStore } from 'pinia'
import { ref } from 'vue'
import { appApi, type AppInfo } from '@/api/app'
import type { Right } from '@/api/rights'

/**
 * Who is signed in and what the server is.
 *
 * Phase 0 has no sign-in: `user` stays null, every right is allowed, so every menu entry and page shows. Phase 1 loads
 * the user, flags and rights from /auth/me (cached for offline use, docs/03 §6.1), adds can()/allows() on them
 * (rights.ts holds/meets), logout, and PNMTD's sessionHandling (401 → login).
 */
export const useSession = defineStore('session', () => {
  const info = ref<AppInfo | null>(null)
  const user = ref<null>(null)
  const loaded = ref(false)

  /** A route's, a drawer entry's or a button's right. Phase 0: all of them. */
  function allows(_right: Right): boolean {
    return true
  }

  /** The server's name and version, in the background: offline the shell opens without them. */
  async function loadInfo() {
    try {
      info.value = await appApi.info()
    } catch {
      // Offline or unreachable: the footer shows no version.
    }
  }

  /** Once per page load; never blocks a navigation (offline, the network may hang). */
  function load() {
    if (loaded.value) return
    loaded.value = true
    void loadInfo()
  }

  return { info, user, loaded, allows, load, loadInfo }
})
