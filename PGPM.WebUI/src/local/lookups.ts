import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useSyncStore } from '@/stores/sync'
import { localDb } from './db'
import type { GroupRow, PersonRow } from './rows'
import { useLive } from './useLive'

/**
 * Small synced lookups the views share: the groups (share picker), the persons by user (owner, who changed what),
 * whether the server can be reached (the online-only actions).
 */

/** The groups, by name. */
export function useGroups() {
  const live = useLive(
    async () =>
      ((await localDb().synced('groups').toArray()) as unknown as GroupRow[])
        .filter((g) => !g.deleted)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [] as GroupRow[],
  )
  return live.value
}

/** The persons that are users (owner transfer, names of users). */
export function useUserPersons() {
  const live = useLive(
    async () =>
      ((await localDb().synced('persons').toArray()) as unknown as PersonRow[]).filter((p) => !p.deleted && !!p.userId),
    [] as PersonRow[],
  )
  const byUser = computed(() => new Map(live.value.value.map((p) => [p.userId!, p])))
  return {
    persons: live.value,
    byUser,
    /** A user's display name, or the fallback. */
    nameOf: (userId: string | null | undefined, fallback = 'Someone') =>
      (userId && byUser.value.get(userId)?.displayName) || fallback,
  }
}

/** Whether the server is reachable as far as this tab knows: the browser is online and the sync isn't offline. */
export function useOnline() {
  const sync = useSyncStore()
  const browser = ref(typeof navigator === 'undefined' ? true : navigator.onLine)
  const update = () => (browser.value = navigator.onLine)
  onMounted(() => {
    window.addEventListener('online', update)
    window.addEventListener('offline', update)
  })
  onBeforeUnmount(() => {
    window.removeEventListener('online', update)
    window.removeEventListener('offline', update)
  })
  return computed(() => browser.value && sync.state !== 'offline')
}

/** What a field is called in the conflict dialog and the history. */
export function fieldLabel(table: string, key: string, type?: string): string {
  const labels: Record<string, string> = {
    title: 'Title',
    description: type === 'note' ? 'Content' : 'Description',
    pinned: 'Pinned',
    color: 'Color',
    statusId: 'Status',
    imageFileId: 'Image',
    value: 'Value',
    notes: 'Notes',
    displayName: 'Name',
  }
  if (labels[key]) return labels[key]
  const words = key
    .replace(/\./g, ' ')
    .replace(/([A-Z])/g, ' $1')
    .toLowerCase()
  return table === 'user_settings' ? key : words.charAt(0).toUpperCase() + words.slice(1)
}
