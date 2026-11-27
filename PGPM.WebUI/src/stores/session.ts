import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { appApi, type AppInfo } from '@/api/app'
import { authApi, type MeResponse, type RightsAreaItem } from '@/api/auth'
import { ApiError } from '@/api/client'
import { holds, meets, setCatalogue, type Area, type Level, type Right } from '@/api/rights'
import { claimLocalData, wipeLocalData } from '@/local/db'
import { requestPersistence } from '@/pwa/storage'

/** Where the last `/auth/me` and the rights catalogue are kept for an offline start (docs/03 §4.5). */
export const sessionCacheKey = 'pgpm.session'

interface Cached {
  me: MeResponse
  catalogue: RightsAreaItem[]
}

function readCache(): Cached | null {
  try {
    const raw = localStorage.getItem(sessionCacheKey)
    const value = raw ? (JSON.parse(raw) as Cached) : null
    return value?.me?.user ? value : null
  } catch {
    return null
  }
}

function writeCache(value: Cached | null) {
  try {
    if (value) localStorage.setItem(sessionCacheKey, JSON.stringify(value))
    else localStorage.removeItem(sessionCacheKey)
  } catch {
    // Storage full or off: the session works without its offline copy.
  }
}

/**
 * Who is signed in, with what rights, flags and session (PNMTD, docs/03 §4.5, §6.1), and what the server is.
 *
 * Offline-first: the last `/auth/me` (and the rights catalogue) is kept in localStorage, so the app opens signed in
 * without the network and checks the session in the background; without a copy the first navigation waits for
 * `/auth/me`. A 401 ends the session (sessionHandling.ts sends the user to the sign-in).
 *
 * The local data (docs/01 §14.5): a sign-out wipes it; a sign-in of another user than the one it belongs to wipes it
 * before the new user sees anything (claimLocalData). A session that merely ended (401) keeps it, so that the same user
 * signs in again and the pending changes go out.
 */
export const useSession = defineStore('session', () => {
  const cached = readCache()
  if (cached) setCatalogue(cached.catalogue)
  const info = ref<AppInfo | null>(null)
  const me = ref<MeResponse | null>(cached?.me ?? null)
  const catalogue = ref<RightsAreaItem[]>(cached?.catalogue ?? [])
  const loaded = ref(false)
  /** The server could not be reached at the last check: the session is the offline copy. */
  const offline = ref(false)

  const user = computed(() => me.value?.user ?? null)
  const flags = computed(() => me.value?.flags ?? null)
  const times = computed(() => me.value?.session ?? null)
  const isAdmin = computed(() => user.value?.isAdmin ?? false)
  const personId = computed(() => user.value?.personId ?? null)
  const groupIds = computed(() => user.value?.groupIds ?? [])

  /** This level in this area, or more, from the rights `/auth/me` returned. */
  function can(area: Area, level: Level): boolean {
    return (
      user.value !== null && holds(user.value.isAdmin, user.value.rights as Partial<Record<Area, Level>>, area, level)
    )
  }

  /** A route's, a drawer entry's or a button's right. */
  function allows(right: Right): boolean {
    return meets(right, isAdmin.value, can)
  }

  function persist() {
    writeCache(me.value ? { me: me.value, catalogue: catalogue.value } : null)
  }

  /** A new answer of `/auth/me` (or the login's), or null: signed out. */
  function set(next: MeResponse | null) {
    me.value = next
    loaded.value = true
    persist()
  }

  async function loadCatalogue() {
    try {
      catalogue.value = await authApi.rights()
      setCatalogue(catalogue.value)
      persist()
    } catch {
      // Held up by a gate (a password to change) or offline: the next load fetches it.
    }
  }

  /** Whether a gate holds the session up (a password to change, a 2FA setup): then only the sign-in screens work. */
  const gated = (next: MeResponse) =>
    next.flags.mustChangePassword || next.flags.totpSetupRequired || next.session.totpDue

  /** A complete sign-in: the session, the catalogue, and the browser asked to keep this origin's data. */
  async function signedIn(next: MeResponse) {
    await claimLocalData(next.user.id)
    set(next)
    void requestPersistence()
    if (!gated(next)) await loadCatalogue()
  }

  /** `/auth/me` now: the user, or signed out (401); offline, the copy stays. */
  async function refresh() {
    try {
      const next = await authApi.me()
      offline.value = false
      // Back from a Microsoft sign-in (a full navigation): the first session of this page asks to keep the data too.
      if (!me.value) void requestPersistence()
      if (next.user.id !== me.value?.user.id) await claimLocalData(next.user.id)
      set(next)
      if (!gated(next)) await loadCatalogue()
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        offline.value = false
        set(null)
      } else {
        offline.value = true
        loaded.value = true
      }
    }
  }

  /**
   * Once per page load, unless forced. With an offline copy it answers at once and checks in the background (a
   * hanging network never blocks a navigation); without one it waits for the server.
   */
  async function load(force = false) {
    if (!info.value && !loaded.value) void loadInfo()
    if (loaded.value && !force) return
    if (!force && me.value) {
      loaded.value = true
      void refresh()
      return
    }
    await refresh()
  }

  /** The server's name and version, in the background: offline the shell opens without them. */
  async function loadInfo() {
    try {
      info.value = await appApi.info()
    } catch {
      // Offline or unreachable: the footer shows no version.
    }
  }

  /**
   * Ends this PGPM session only: a Microsoft session at Entra stays (no single sign-out, docs/03 §5.3). The local data
   * goes with it (the caller warned about pending changes and conflicts).
   */
  async function logout() {
    try {
      await authApi.logout()
    } catch {
      // Signed out on the server already, or unreachable: the session ends here either way.
    }
    await wipeLocalData()
    set(null)
  }

  return {
    info,
    me,
    user,
    flags,
    times,
    catalogue,
    loaded,
    offline,
    isAdmin,
    personId,
    groupIds,
    can,
    allows,
    set,
    signedIn,
    refresh,
    load,
    loadInfo,
    logout,
  }
})
