import type { Resolved } from '@/local/conflicts'
import type { Changes, OutboxOp, Row } from '@/local/outbox'
import type { Access } from '@/local/rows'
import { call, client, type Schemas } from './client'

/**
 * The sync contract (docs/03 §11.4) and the online-only endpoints of phase 2 (§6.3, §11.6, §18.6), on the types
 * generated from openapi.json. What the client sends is typed strictly here (the generated request types have every
 * member optional) and checked against the generated ones where it is sent; the live channel's messages are typed in
 * local/live.ts.
 */

/** An outbox entry as pushed (data model §14.1): without the local `seq` and `sentAt`. */
export interface WireMutation {
  mutationId: string
  batch: string
  table: string
  id: string
  op: OutboxOp
  baseRev: number | null
  changes: Changes | null
  row: Row | null
  clientChangedAt: string
  force?: boolean
}

/** `POST /api/v1/sync` as the engine sends it. */
export interface SyncRequest {
  clientId: string
  appVersion: string
  since: number
  accessEpoch: number
  limit: number
  mutations: WireMutation[]
  resolved: Resolved[]
  pull: boolean
  pending: number
  conflicts: number
}

export type SyncResponse = Schemas['SyncResponse']
/** `accepted`, `partial`, `conflict` (with `kind`) or `rejected` (with `code`; no `current`: drop the local row). */
export type MutationResult = Schemas['SyncMutationResult']
/** A key the server didn't apply because someone else changed it first. */
export type FieldConflict = Schemas['SyncConflict']
/** Access to a root (`item`, `map`) was lost: it goes from the device with everything below it. */
export type Eviction = Schemas['SyncEviction']

/** Milliseconds until a call counts as unanswered (offline): the sync's 60 s (docs/03 §11.7). */
function timeoutSignal(ms: number, outer?: AbortSignal): { signal: AbortSignal; done: () => void } {
  const controller = new AbortController()
  const abort = () => controller.abort()
  const timer = setTimeout(abort, ms)
  outer?.addEventListener('abort', abort)
  if (outer?.aborted) abort()
  return {
    signal: controller.signal,
    done: () => {
      clearTimeout(timer)
      outer?.removeEventListener('abort', abort)
    },
  }
}

/** The push and pull: 60 s, a timeout counts as offline (an {@link ApiError} with status 0). */
export async function postSync(body: SyncRequest, signal?: AbortSignal): Promise<SyncResponse> {
  const timeout = timeoutSignal(60_000, signal)
  try {
    return await call(client.POST('/api/v1/sync', { body, signal: timeout.signal, cache: 'no-store' }))
  } finally {
    timeout.done()
  }
}

/** The live channel's URL on this origin: wss behind TLS. */
export function liveUrl(): string {
  const { protocol, host } = location
  return `${protocol === 'https:' ? 'wss' : 'ws'}://${host}/api/v1/sync/live`
}

// --- sharing (docs/03 §6.3) ----------------------------------------------------------------------------------------

/** A group's access to an item, as the share editor holds it. */
export interface ShareSpec {
  groupId: string
  access: Access
}

export type ItemShare = Schemas['ItemShareItem']

export const sharingApi = {
  /** The full list of shares (needs *manage*, online); answers the new list. */
  setShares: (itemId: string, shares: ShareSpec[]) =>
    call(client.PUT('/api/v1/items/{id}/shares', { params: { path: { id: itemId } }, body: shares })),
  transferOwner: (itemId: string, userId: string) =>
    call(client.POST('/api/v1/items/{id}/owner', { params: { path: { id: itemId } }, body: { userId } })),
  /** A map's shares (docs/01 §12.1, same rules as an item's). */
  setMapShares: (mapId: string, shares: ShareSpec[]) =>
    call(client.PUT('/api/v1/maps/{id}/shares', { params: { path: { id: mapId } }, body: shares })),
  transferMapOwner: (mapId: string, userId: string) =>
    call(client.POST('/api/v1/maps/{id}/owner', { params: { path: { id: mapId } }, body: { userId } })),
}

// --- trash (docs/03 §11.6) -----------------------------------------------------------------------------------------

export type TrashEntry = Schemas['TrashItem']

export const trashApi = {
  list: (query: { page?: number; pageSize?: number; search?: string; sort?: string } = {}) =>
    call(client.GET('/api/v1/trash', { params: { query } })),
  restore: (batchId: string) => call(client.POST('/api/v1/trash/{batchId}/restore', { params: { path: { batchId } } })),
  purge: (batchId: string) => call(client.DELETE('/api/v1/trash/{batchId}', { params: { path: { batchId } } })),
}

// --- history (docs/02 §12) -----------------------------------------------------------------------------------------

/** `changes`: `[{field, old, new}]`, a share's `[{field: "share", groupId, old, new}]`, a conflict's `{field, yours,
 * theirs, by}`, a resolution's `{field, resolution}` (field null: the whole row). */
export type HistoryEntry = Schemas['HistoryEntryItem']

export const historyApi = {
  list: (query: { itemId: string; page?: number; pageSize?: number }) =>
    call(client.GET('/api/v1/history', { params: { query } })),
}

// --- who else is editing (docs/03 §18.6) ---------------------------------------------------------------------------

export const editingApi = {
  start: (itemId: string, target: string) => call(client.POST('/api/v1/editing', { body: { itemId, target } })),
  /** The DELETE carries its body too. */
  stop: (itemId: string, target: string) => call(client.DELETE('/api/v1/editing', { body: { itemId, target } })),
}
