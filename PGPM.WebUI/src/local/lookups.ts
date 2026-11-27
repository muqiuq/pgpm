import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useSyncStore } from '@/stores/sync'
import { localDb } from './db'
import { defaultRoles } from './items'
import type { AssignmentRoleRow, GroupRow, PersonRow, StatusRow } from './rows'
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

/** Every status (archived ones too: still displayed), by id and in order per type. */
export function useStatuses() {
  const live = useLive(
    async () =>
      ((await localDb().synced('statuses').toArray()) as unknown as StatusRow[])
        .filter((s) => !s.deleted)
        .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)),
    [] as StatusRow[],
  )
  const byId = computed(() => new Map(live.value.value.map((s) => [s.id, s])))
  return { all: live.value, byId, loaded: live.loaded }
}

/** Every person (inactive ones too: still shown where assigned), by id. */
export function usePersons() {
  const live = useLive(
    async () =>
      ((await localDb().synced('persons').toArray()) as unknown as PersonRow[])
        .filter((p) => !p.deleted)
        .sort((a, b) => a.displayName.localeCompare(b.displayName)),
    [] as PersonRow[],
  )
  const byId = computed(() => new Map(live.value.value.map((p) => [p.id, p])))
  return { all: live.value, byId }
}

/** The role suggestions: the configured ones (`assignment_roles`, phase 4), else docs/01 §4.1's defaults. */
export function useRoleSuggestions() {
  const live = useLive(
    async () =>
      ((await localDb().synced('assignment_roles').toArray()) as unknown as AssignmentRoleRow[])
        .filter((r) => !r.deleted && !r.isArchived)
        .sort((a, b) => a.sortOrder - b.sortOrder)
        .map((r) => r.name),
    [] as string[],
  )
  return computed(() => (live.value.value.length ? live.value.value : defaultRoles))
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
    leadOrganisation: 'Organisation',
    leadValue: 'Estimated value',
    leadCurrency: 'Currency',
    leadProbability: 'Probability',
    leadDecisionDate: 'Expected decision date',
    leadNextAction: 'Next action',
    leadNextActionDue: 'Next action due',
    leadSource: 'Source',
    projectStartDate: 'Start date',
    projectEndDate: 'End date',
    fileName: 'File name',
    role: 'Role',
  }
  if (labels[key]) return labels[key]
  const words = key
    .replace(/\./g, ' ')
    .replace(/([A-Z])/g, ' $1')
    .toLowerCase()
  return table === 'user_settings' ? key : words.charAt(0).toUpperCase() + words.slice(1)
}
