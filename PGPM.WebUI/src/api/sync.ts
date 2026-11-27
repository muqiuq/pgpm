import type { Resolved } from '@/local/conflicts'
import type { OutboxOp, Row } from '@/local/outbox'
import type { Access } from '@/local/rows'
import { rawCall } from './raw'

/**
 * The sync contract (docs/03 §11.4) and the online-only endpoints of phase 2 (§6.3, §11.6, §18.6), typed by hand until
 * the server's OpenAPI document has them. JSON names are camelCase.
 */

/** An outbox entry as pushed (data model §14.1): without the local `seq` and `sentAt`. */
export interface WireMutation {
  mutationId: string
  batch: string
  table: string
  id: string
  op: OutboxOp
  baseRev: number | null
  changes: Record<string, { old: unknown; new: unknown }> | null
  row: Row | null
  clientChangedAt: string
  force?: boolean
}

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

export type MutationStatus = 'accepted' | 'partial' | 'conflict' | 'rejected'

export interface MutationResult {
  mutationId: string
  status: MutationStatus
  id?: string
  rev?: number
  seq?: number
  /** accepted: a duplicate of a unique pair, merged into this existing row. */
  mergedInto?: string
  /** partial: the keys that were not applied. */
  conflictFields?: string[]
  kind?: 'deleted_by_other' | 'changed_since_delete'
  code?: string
  message?: string
  /** conflict / rejected: the server's row now (a tombstone, or null when it has none the user may see). */
  current?: Row | null
}

/** A key the server didn't apply because someone else changed it first. */
export interface FieldConflict {
  mutationId: string
  table: string
  id: string
  field: string
  theirs: unknown
  by: string | null
  at: string | null
  rev: number
}

export interface Eviction {
  kind: 'item' | 'map'
  id: string
}

export interface SyncResponse {
  head: number
  cursor: number
  more: boolean
  reset: boolean
  accessEpoch: number
  results: MutationResult[]
  conflicts: FieldConflict[]
  changes: Record<string, Row[]>
  evictions: Eviction[]
}

/** The push and pull: 60 s, a timeout counts as offline (docs/03 §11.7). */
export function postSync(body: SyncRequest, signal?: AbortSignal): Promise<SyncResponse> {
  return rawCall<SyncResponse>('POST', '/api/v1/sync', { body, signal, timeout: 60_000 })
}

/** The live channel's URL on this origin: wss behind TLS. */
export function liveUrl(): string {
  const { protocol, host } = location
  return `${protocol === 'https:' ? 'wss' : 'ws'}://${host}/api/v1/sync/live`
}

// --- sharing (docs/03 §6.3) ----------------------------------------------------------------------------------------

export interface ShareSpec {
  groupId: string
  access: Access
}

export const sharingApi = {
  /** The full list of shares (needs *manage*, online). */
  setShares: (itemId: string, shares: ShareSpec[]) =>
    rawCall<unknown>('PUT', `/api/v1/items/${encodeURIComponent(itemId)}/shares`, { body: shares }),
  transferOwner: (itemId: string, userId: string) =>
    rawCall<unknown>('POST', `/api/v1/items/${encodeURIComponent(itemId)}/owner`, { body: { userId } }),
}

// --- trash (docs/03 §11.6) -----------------------------------------------------------------------------------------

export interface TrashEntry {
  batchId: string
  rootKind: 'item' | 'map'
  rootId: string
  title: string
  deletedAt: string
  deletedBy: string | null
  deletedByName: string | null
  /** The item's type, if the server says. */
  type?: string | null
}

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

export const trashApi = {
  list: (query: { page?: number; pageSize?: number; search?: string; sort?: string } = {}) =>
    rawCall<Paged<TrashEntry>>('GET', '/api/v1/trash', { query }),
  restore: (batchId: string) => rawCall<unknown>('POST', `/api/v1/trash/${encodeURIComponent(batchId)}/restore`),
  purge: (batchId: string) => rawCall<unknown>('DELETE', `/api/v1/trash/${encodeURIComponent(batchId)}`),
}

// --- history (docs/02 §12) -----------------------------------------------------------------------------------------

export interface HistoryChange {
  field: string
  old?: unknown
  new?: unknown
  [key: string]: unknown
}

export interface HistoryEntry {
  id: number | string
  at: string
  userId: string | null
  userName?: string | null
  source: string
  entity: string
  entityId: string
  action: string
  changes: HistoryChange[] | Record<string, unknown> | null
}

export const historyApi = {
  list: (query: { itemId: string; page?: number; pageSize?: number }) =>
    rawCall<Paged<HistoryEntry>>('GET', '/api/v1/history', { query }),
}

// --- who else is editing (docs/03 §18.6) ---------------------------------------------------------------------------

export const editingApi = {
  start: (itemId: string, target: string) => rawCall<unknown>('POST', '/api/v1/editing', { body: { itemId, target } }),
  stop: (itemId: string, target: string) => rawCall<unknown>('DELETE', '/api/v1/editing', { body: { itemId, target } }),
}
