import { ApiError } from '@/api/client'
import type {
  Eviction,
  FieldConflict,
  ItemShare,
  MutationResult,
  ShareSpec,
  SyncRequest,
  SyncResponse,
  TrashEntry,
  WireMutation,
} from '@/api/sync'
import type { EditingMessage, SocketLike } from '@/local/live'
import { applyPatch, getPath, jsonEqual, type Row } from '@/local/outbox'
import { accessRank, type Access } from '@/local/rows'
import { uuidv7 } from '@/local/uuid'

/**
 * An in-memory server for the sync tests: the contract of docs/03 §11.4–§11.6 as PGPM's server (`PGPM/Sync`) answers
 * it – push with per-field conflicts (`partial`, `conflict` of kind `field` with `current`), delete conflicts and
 * `force`, idempotent mutation ids, `missing_access` without `current` for a row the user can't see, duplicate pairs
 * (`mergedInto`; `user_settings` overwrite the value), sharing visibility with computed `access`, re-stamps and
 * evictions, access epochs and resets (`cursor: 0`, no rows), trash and restore (an item's shares stay out of its
 * batch), and the live channel: `reset` then close 4409, `session` then 4401, ack flow control (an ack below the sent
 * cursor is ignored), the server's `ping`, *… is editing* notices never sent to the editor's own user. Timestamps with
 * microseconds, as PostgreSQL keeps them. Phase 3: every synced table with the server's descriptors (whitelists,
 * server-owned members, the items', persons', saved views' and children's rules), the file transfer (chunks in order,
 * the commit's checks, PNG metadata stripped, shared bytes) and the statuses endpoints with the server's codes and
 * answers. Checked against the real server by the e2e tests (e2e/*.spec.ts) and the contract tests (PGPM.Contract.Tests).
 */

type ServerRow = Row & { id: string; seq: number; rev: number; deleted: boolean; deleteBatchId: string | null }

interface User {
  id: string
  name: string
  groupIds: string[]
  isAdmin: boolean
  accessEpoch: number
  /** Configuration: Manage (shared saved views, the statuses endpoints). */
  configManage: boolean
}

/**
 * The patchable whitelist per table, as the server's table descriptors have it (docs/03 §11.5; the `SyncTable` classes of `PGPM/`);
 * `key.*` admits the members of a jsonb column. Others: read-only or insert-only.
 */
export const patchable: Record<string, readonly string[]> = {
  items: [
    'title',
    'description',
    'color',
    'imageFileId',
    'statusId',
    'pinned',
    'tags',
    'leadOrganisation',
    'leadValue',
    'leadCurrency',
    'leadProbability',
    'leadDecisionDate',
    'leadNextAction',
    'leadNextActionDue',
    'leadSource',
    'projectStartDate',
    'projectEndDate',
  ],
  persons: [
    'displayName',
    'givenName',
    'surname',
    'email',
    'userPrincipalName',
    'userType',
    'jobTitle',
    'department',
    'companyName',
    'phone',
    'mobilePhone',
    'isActive',
    'notes',
  ],
  user_settings: ['value', 'value.*'],
  assignments: ['role', 'sortOrder'],
  item_links: [],
  files: ['fileName', 'thumbnail'],
  field_values: ['value'],
  item_sections: ['sortOrder'],
  saved_views: ['name', 'description', 'definition', 'definition.*', 'isShared', 'sortOrder'],
  milestones: [
    'title',
    'description',
    'dueDate',
    'status',
    'decision',
    'decisionNote',
    'onGoStatusId',
    'onNoGoStatusId',
    'color',
    'sortOrder',
  ],
  dod_items: ['text', 'state', 'note', 'sortOrder'],
  maps: [
    'name',
    'description',
    'settings',
    'settings.*',
    'matrix',
    'matrix.*',
    'defaultView',
    'defaultView.*',
    'isTemplate',
    'preview',
  ],
  map_layers: ['name', 'sortOrder', 'isVisible', 'isLocked'],
  map_elements: [
    'layerId',
    'z',
    'x',
    'y',
    'width',
    'height',
    'text',
    'points',
    'startElementId',
    'endElementId',
    'style',
    'style.*',
    'config',
    'config.*',
  ],
}

/** Whether a patch may change the key: in the whitelist, or a member of a jsonb column whose `key.*` is. */
export function isPatchable(table: string, key: string): boolean {
  const allowed = patchable[table] ?? []
  if (allowed.includes(key)) return true
  const dot = key.indexOf('.')
  return dot > 0 && allowed.includes(`${key.slice(0, dot)}.*`) && key.split('.').every((p) => p && p !== '__proto__')
}

/**
 * The members the server sets itself: ignored in an insert's row (and not patchable). `access` and `canEdit` are
 * computed per user, `storagePath` and `unreferencedSince` never leave the server.
 */
const serverOwned: Record<string, readonly string[]> = {
  items: ['number', 'ownerUserId', 'statusChangedAt', 'convertedToItemId', 'access'],
  persons: ['source', 'entraTenantId', 'entraObjectId', 'directorySyncedAt', 'userId'],
  files: ['status', 'storagePath', 'unreferencedSince'],
  milestones: ['completedAt', 'decidedAt', 'decidedBy'],
  dod_items: ['stateChangedBy', 'stateChangedAt'],
  saved_views: ['ownerUserId'],
  maps: ['ownerUserId', 'access'],
  user_settings: ['userId'],
}

/** Every synced table, in the server's order (parents first). */
export const tableNames = [
  'groups',
  'persons',
  'statuses',
  'sections',
  'field_definitions',
  'project_templates',
  'template_statuses',
  'template_sections',
  'template_field_defaults',
  'template_milestones',
  'assignment_roles',
  'items',
  'item_shares',
  'assignments',
  'item_links',
  'files',
  'field_values',
  'item_sections',
  'milestones',
  'dod_items',
  'saved_views',
  'maps',
  'map_shares',
  'map_layers',
  'map_elements',
  'user_settings',
]

/** Read-only for clients: written by admin endpoints (data model §7). */
const readOnly = new Set([
  'groups',
  'statuses',
  'sections',
  'field_definitions',
  'project_templates',
  'template_statuses',
  'template_sections',
  'template_field_defaults',
  'template_milestones',
  'assignment_roles',
])

/** Tables whose rows hang below an item by `itemId` (access, delete cascade). */
const itemChildren = ['assignments', 'files', 'field_values', 'item_sections', 'milestones']

/** Tables whose rows hang below a map by `mapId`. */
const mapChildren = ['map_shares', 'map_layers', 'map_elements', 'files']

/** The pairs that are unique where not deleted; a duplicate insert is merged into the existing row (docs/03 §11.5). */
const samePair: Record<string, readonly string[]> = {
  assignments: ['itemId', 'milestoneId', 'personId'],
  item_links: ['fromItemId', 'toItemId', 'relation'],
  item_sections: ['itemId', 'sectionId'],
  item_shares: ['itemId', 'groupId'],
  map_shares: ['mapId', 'groupId'],
}

/** The file transfer's chunk size (docs/03 §10.2). */
export const chunkBytes = 4 * 1024 * 1024
/** Files:MaxAttachmentBytes, Files:MaxImageBytes, Files:BlockedExtensions (docs/03 §10.1). */
const maxAttachment = 25 * 1024 * 1024
const maxImage = 2 * 1024 * 1024
const blockedExtensions = ['exe', 'msi', 'dll', 'scr', 'com', 'bat', 'cmd', 'ps1', 'vbs', 'js', 'jar', 'hta', 'lnk']
/** The purposes that are images, checked by their bytes at the commit (FilesOptions.IsImage). */
const imagePurposes = ['cover', 'map_image', 'embedded']
/** The image types and their extensions (ImageCheck.ExtensionOf). */
const imageExtensions: Record<string, string> = {
  'image/png': 'png',
  'image/jpeg': 'jpg',
  'image/gif': 'gif',
  'image/webp': 'webp',
}

/** The blocked extension in a name – double ones too (`invoice.exe.pdf`) – or null. */
function blockedIn(name: string): string | null {
  const parts = name.toLowerCase().split('.').slice(1)
  return parts.find((p) => blockedExtensions.includes(p.trim())) ?? null
}

/** An image's name ends in its type's extension (FileNames.ImageName): `Screenshot` → `Screenshot.png`. */
function imageName(name: string, extension: string): string {
  const aliases: Record<string, string[]> = { png: ['png'], jpg: ['jpg', 'jpeg', 'jpe', 'jfif'], gif: ['gif'] }
  const dot = name.lastIndexOf('.')
  const current = dot > 0 ? name.slice(dot + 1) : ''
  if ((aliases[extension] ?? [extension]).includes(current.toLowerCase())) return name
  const stem = /^(?=.*[a-z])[a-z0-9]{1,10}$/i.test(current) ? name.slice(0, dot) : name
  return `${stem}.${extension}`
}

const startsWith = (bytes: Uint8Array, prefix: number[], at = 0) => prefix.every((b, i) => bytes[at + i] === b)

/**
 * The commit's image check (ImageCheck), as far as the tests need it: the type from the magic bytes, PNG's size from its
 * header and its metadata chunks (tEXt, zTXt, iTXt, eXIf, tIME) removed without re-encoding; null when it isn't one.
 */
function checkImage(
  bytes: Uint8Array,
): { bytes: Uint8Array; contentType: string; width?: number; height?: number } | null {
  if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
    const kept: Uint8Array[] = [bytes.slice(0, 8)]
    let at = 8
    while (at + 12 <= bytes.length) {
      const length = view.getUint32(at)
      const type = String.fromCharCode(...bytes.slice(at + 4, at + 8))
      const end = at + 12 + length
      if (end > bytes.length) return null
      if (!['tEXt', 'zTXt', 'iTXt', 'eXIf', 'tIME'].includes(type)) kept.push(bytes.slice(at, end))
      at = end
    }
    const out = new Uint8Array(kept.reduce((n, k) => n + k.length, 0))
    let o = 0
    for (const k of kept) {
      out.set(k, o)
      o += k.length
    }
    return { bytes: out, contentType: 'image/png', width: view.getUint32(16), height: view.getUint32(20) }
  }
  if (startsWith(bytes, [0xff, 0xd8, 0xff])) return { bytes, contentType: 'image/jpeg' }
  if (startsWith(bytes, [0x47, 0x49, 0x46, 0x38])) return { bytes, contentType: 'image/gif' }
  if (startsWith(bytes, [0x52, 0x49, 0x46, 0x46]) && startsWith(bytes, [0x57, 0x45, 0x42, 0x50], 8))
    return { bytes, contentType: 'image/webp' }
  return null
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes as Uint8Array<ArrayBuffer>)
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T

interface Stored {
  result: MutationResult
  conflicts: FieldConflict[]
}

export class FakeSyncServer {
  seq = 0
  online = true
  horizon = 0
  readonly users = new Map<string, User>()
  readonly rows = new Map<string, Map<string, ServerRow>>(tableNames.map((t) => [t, new Map()]))
  readonly requests: SyncRequest[] = []
  readonly resolved: { userId: string; table: string; id: string; field: string | null; resolution: string }[] = []
  private readonly mutations = new Map<string, Stored>()
  private readonly evictions: { userId: string; seq: number; kind: 'item' | 'map'; id: string }[] = []
  /** Open *… is editing* notices (EditingRegistry). */
  readonly editing: EditingMessage[] = []
  private readonly lastChange = new Map<string, { by: string; at: string }>()
  private readonly batches = new Map<
    string,
    { rootKind: 'item' | 'map'; rootId: string; at: string; by: string; restored: boolean }
  >()
  private readonly created = new Map<string, Set<string>>()
  private readonly numbers = new Map<string, number>()
  readonly connections = new Set<ServerConnection>()
  /** Delay of an HTTP answer in ms (the real one is a network round trip). */
  latency = 0

  /** Chunked uploads in progress (`file_uploads`), by file id. */
  readonly uploads = new Map<string, Uint8Array[]>()
  /** The stored bytes of committed files. */
  readonly fileBytes = new Map<string, { bytes: Uint8Array; type: string }>()
  /** Every REST call of {@link fetch}: method and path. */
  readonly calls: string[] = []

  addUser(user: { id: string; name: string; groupIds?: string[]; isAdmin?: boolean; configManage?: boolean }): User {
    const full: User = { groupIds: [], isAdmin: false, accessEpoch: 1, configManage: false, ...user }
    this.users.set(user.id, full)
    return full
  }

  addGroup(id: string, name: string) {
    this.write('groups', { id, name, description: null }, null)
  }

  /** A row as an admin job would write it (seeded). */
  seed(table: string, row: Row & { id: string }, by: string | null = null): ServerRow {
    return this.write(table, row, by)
  }

  table(name: string): Map<string, ServerRow> {
    return this.rows.get(name)!
  }

  private user(id: string): User {
    const user = this.users.get(id)
    if (!user) throw new ApiError(401, { code: 'not_signed_in' })
    return user
  }

  /** The wire's timestamp (docs/03 §11.4): UTC, microseconds. */
  private now() {
    return new Date().toISOString().replace('Z', '000Z')
  }

  /** Inserts or overwrites a row with fresh seq/rev/updated_*. */
  private write(table: string, row: Row & { id: string }, by: string | null): ServerRow {
    const existing = this.table(table).get(row.id)
    const now = this.now()
    const next: ServerRow = {
      deleted: false,
      deleteBatchId: null,
      createdAt: existing?.createdAt ?? now,
      createdBy: existing?.createdBy ?? by,
      ...existing,
      ...row,
      seq: ++this.seq,
      rev: (existing?.rev ?? 0) + 1,
      updatedAt: now,
      updatedBy: by,
    }
    this.table(table).set(row.id, next)
    return next
  }

  /** A re-stamp: a new seq, the same rev (docs/03 §11.6). */
  private restamp(table: string, id: string) {
    const row = this.table(table).get(id)
    if (row) row.seq = ++this.seq
  }

  // --- access ---------------------------------------------------------------------------------------------------

  itemAccess(user: User, item: ServerRow): Access | null {
    if (user.isAdmin || item.ownerUserId === user.id) return 'manage'
    let best: Access | null = null
    // An item's shares are not deleted with it: they decide who sees the tombstone (docs/03 §15 #32).
    for (const share of this.table('item_shares').values()) {
      if (share.itemId !== item.id || share.deleted || !user.groupIds.includes(share.groupId as string)) continue
      const level = share.access as Access
      if (!best || accessRank[level] > accessRank[best]) best = level
    }
    return best
  }

  mapAccess(user: User, map: ServerRow): Access | null {
    if (user.isAdmin || map.ownerUserId === user.id) return 'manage'
    let best: Access | null = map.isTemplate === true ? 'read' : null
    for (const share of this.table('map_shares').values()) {
      if (share.mapId !== map.id || share.deleted || !user.groupIds.includes(share.groupId as string)) continue
      const level = share.access as Access
      if (!best || accessRank[level] > accessRank[best]) best = level
    }
    return best
  }

  /** The item or map a row hangs below (its access decides), or null for a row of its own. */
  private ownerOf(table: string, row: Row): { kind: 'item' | 'map'; row: ServerRow | undefined } | null {
    if (table === 'items') return { kind: 'item', row: row as ServerRow }
    if (table === 'maps') return { kind: 'map', row: row as ServerRow }
    if (table === 'item_links') return { kind: 'item', row: this.table('items').get(row.fromItemId as string) }
    if (table === 'dod_items' || (table === 'assignments' && row.milestoneId)) {
      const milestone = this.table('milestones').get(row.milestoneId as string)
      return { kind: 'item', row: milestone ? this.table('items').get(milestone.itemId as string) : undefined }
    }
    if (table === 'files' && row.mapId) return { kind: 'map', row: this.table('maps').get(row.mapId as string) }
    if (table === 'item_shares' || itemChildren.includes(table))
      return { kind: 'item', row: this.table('items').get(row.itemId as string) }
    if (mapChildren.includes(table)) return { kind: 'map', row: this.table('maps').get(row.mapId as string) }
    return null
  }

  /** The user's access to the item or map a row belongs to (`manage` for rows of their own kind). */
  private accessTo(user: User, table: string, row: Row): Access | null {
    const owner = this.ownerOf(table, row)
    if (!owner) return null
    if (!owner.row) return null
    return owner.kind === 'item' ? this.itemAccess(user, owner.row) : this.mapAccess(user, owner.row)
  }

  private visible(user: User, table: string, row: ServerRow): boolean {
    if (table === 'user_settings') return row.userId === user.id
    if (table === 'saved_views') return row.ownerUserId === user.id || row.isShared === true
    if (!this.ownerOf(table, row)) return true
    return this.accessTo(user, table, row) !== null
  }

  private wireRow(user: User, table: string, row: ServerRow): Row {
    if (row.deleted) return { id: row.id, seq: row.seq, rev: row.rev, deleted: true }
    const out: Row = clone(row)
    if (table === 'items') out.access = this.itemAccess(user, row)
    if (table === 'maps') out.access = this.mapAccess(user, row)
    if (table === 'sections') out.canEdit = true
    delete out.storagePath
    return out
  }

  /** The `current` of a result: a tombstone also names its delete batch (for *Restore*). */
  private currentRow(user: User, table: string, row: ServerRow): Row {
    const out = this.wireRow(user, table, row)
    return row.deleted ? { ...out, deleteBatchId: row.deleteBatchId } : out
  }

  // --- pull -----------------------------------------------------------------------------------------------------

  pull(userId: string, since: number, limit: number) {
    const user = this.user(userId)
    const changes: Record<string, Row[]> = {}
    const truncated: number[] = []
    for (const table of tableNames) {
      const candidates = [...this.table(table).values()]
        .filter((r) => r.seq > since && this.visible(user, table, r))
        .sort((a, b) => a.seq - b.seq)
      const page = candidates.slice(0, limit)
      if (page.length) changes[table] = page.map((r) => this.wireRow(user, table, r))
      if (candidates.length > limit) truncated.push(page[page.length - 1]!.seq)
    }
    const mine = this.evictions.filter((e) => e.userId === userId && e.seq > since).sort((a, b) => a.seq - b.seq)
    const evictions = mine.slice(0, limit)
    if (mine.length >= limit) truncated.push(evictions[evictions.length - 1]!.seq)
    const more = truncated.length > 0
    const cursor = more ? Math.max(since, Math.min(...truncated)) : this.seq
    return { changes, evictions: evictions.map((e): Eviction => ({ kind: e.kind, id: e.id })), cursor, more }
  }

  // --- push -----------------------------------------------------------------------------------------------------

  /** `POST /api/v1/sync`. */
  async sync(userId: string, request: SyncRequest): Promise<SyncResponse> {
    if (this.latency) await new Promise((r) => setTimeout(r, this.latency))
    if (!this.online) throw new ApiError(0, undefined)
    const body = clone(request)
    this.requests.push(body)
    const user = this.user(userId)
    const results: MutationResult[] = []
    const conflicts: FieldConflict[] = []
    for (const mutation of body.mutations) {
      let stored = this.mutations.get(mutation.mutationId)
      if (!stored) {
        stored = this.apply(user, mutation)
        this.mutations.set(mutation.mutationId, stored)
      }
      results.push(stored.result)
      conflicts.push(...stored.conflicts)
    }
    for (const r of body.resolved) this.resolved.push({ userId, ...r })
    // A device that has synced before resets on another epoch, or behind the tombstone horizon (or beyond the head).
    const reset =
      body.since > 0 && (body.accessEpoch !== user.accessEpoch || body.since < this.horizon || body.since > this.seq)
    const response: SyncResponse = {
      head: this.seq,
      cursor: reset ? 0 : body.since,
      more: false,
      reset,
      accessEpoch: user.accessEpoch,
      results,
      conflicts,
      changes: {},
      evictions: [],
    }
    if (body.pull && !reset) {
      const page = this.pull(userId, body.since, body.limit)
      Object.assign(response, page)
    }
    if (body.mutations.length) this.publish()
    return clone(response)
  }

  /** Refused; without `current` the client drops its row (a row the user can't see, a refused insert). */
  private rejected(
    mutation: WireMutation,
    code: string,
    current: Row | null = null,
    errors?: Record<string, string[]>,
  ): Stored {
    const result: MutationResult = { mutationId: mutation.mutationId, status: 'rejected', id: mutation.id, code }
    result.message = code
    if (errors) result.errors = errors
    if (current) result.current = current
    return { result, conflicts: [] }
  }

  private conflict(
    mutation: WireMutation,
    row: ServerRow,
    kind: string,
    current: Row,
    extra: Partial<MutationResult> = {},
  ): Stored {
    return {
      result: {
        mutationId: mutation.mutationId,
        status: 'conflict',
        id: row.id,
        rev: row.rev,
        seq: row.seq,
        kind,
        current,
        ...extra,
      },
      conflicts: [],
    }
  }

  private accepted(mutation: WireMutation, row: ServerRow, extra: Partial<MutationResult> = {}): Stored {
    return {
      result: { mutationId: mutation.mutationId, status: 'accepted', id: row.id, rev: row.rev, seq: row.seq, ...extra },
      conflicts: [],
    }
  }

  private canWrite(user: User, table: string, row: Row, level: Access): boolean {
    if (table === 'user_settings') return row.userId === user.id
    if (table === 'saved_views') return row.isShared === true ? user.configManage : row.ownerUserId === user.id
    if (table === 'persons') return true
    const access = this.accessTo(user, table, row)
    // Children of an item or map are written (and deleted) with edit on it; the item or map itself as asked.
    const needed: Access = table === 'items' || table === 'maps' ? level : 'edit'
    return !!access && accessRank[access] >= accessRank[needed]
  }

  private apply(user: User, m: WireMutation): Stored {
    const rows = this.rows.get(m.table)
    if (!rows) return this.rejected(m, 'not_supported')
    if (readOnly.has(m.table)) return this.rejected(m, m.op === 'patch' ? 'not_patchable' : 'not_supported')
    const existing = rows.get(m.id)
    if (m.op === 'insert') return this.insert(user, m, existing)
    // A row that doesn't exist and one the user may not see are the same answer: no current, the client drops it.
    if (!existing || !this.visible(user, m.table, existing)) return this.rejected(m, 'missing_access')
    if (m.op === 'patch') return this.patch(user, m, existing)
    return this.remove(user, m, existing)
  }

  private insert(user: User, m: WireMutation, existing: ServerRow | undefined): Stored {
    if (existing) return this.rejected(m, 'duplicate_id')
    const row: Row & { id: string } = { ...(m.row ?? {}), id: m.id }
    // The server's own members and the computed ones: ignored as the server ignores them (docs/03 §11.4).
    for (const key of serverOwned[m.table] ?? []) delete row[key]
    if (m.table === 'items') return this.insertItem(user, m, row)
    if (m.table === 'item_shares' || m.table === 'map_shares') {
      const parentTable = m.table === 'item_shares' ? 'items' : 'maps'
      const parent = this.table(parentTable).get((row.itemId ?? row.mapId) as string)
      if (!parent || parent.deleted) return this.rejected(m, 'parent_missing')
      if (parent.ownerUserId !== user.id || !this.created.get(m.batch)?.has(parent.id)) {
        return this.rejected(m, 'missing_access')
      }
      return this.insertPair(user, m, row)
    }
    if (m.table === 'maps') {
      const stored = this.write('maps', { ...row, ownerUserId: user.id }, user.id)
      this.remember(m.batch, m.id)
      return this.accepted(m, stored)
    }
    if (m.table === 'saved_views') {
      if (row.isShared === true && !user.configManage) return this.rejected(m, 'missing_right')
      return this.accepted(m, this.write('saved_views', { ...row, ownerUserId: user.id }, user.id))
    }
    if (this.ownerOf(m.table, row)) {
      const owner = this.ownerOf(m.table, row)!
      // A parent that is gone and one the user may not see are the same answer (RequireParentItemAsync).
      if (!owner.row || owner.row.deleted || !this.accessTo(user, m.table, row))
        return this.rejected(m, 'parent_missing')
      if (!this.canWrite(user, m.table, row, 'edit')) return this.rejected(m, 'missing_access')
      if (m.table === 'item_links') {
        if (row.toItemId === row.fromItemId)
          return this.rejected(m, 'validation_failed', null, { toItemId: ['An item doesn’t link to itself.'] })
        const target = this.table('items').get(row.toItemId as string)
        if (!target || target.deleted || !this.itemAccess(user, target)) return this.rejected(m, 'parent_missing')
      }
      if (m.table === 'assignments') {
        const person = this.table('persons').get(row.personId as string)
        if (!person || person.deleted) return this.rejected(m, 'parent_missing')
      }
      if (m.table === 'files') return this.insertFile(user, m, row)
      if (m.table === 'milestones') {
        row.completedAt = row.status === 'done' ? this.now() : null
        this.decided(user, row)
      }
      if (m.table === 'dod_items') {
        const changed = (row.state ?? 'open') !== 'open'
        row.stateChangedAt = changed ? this.now() : null
        row.stateChangedBy = changed ? user.id : null
      }
      if (m.table === 'field_values') {
        const duplicate = [...this.table('field_values').values()].find(
          (v) => !v.deleted && v.itemId === row.itemId && v.fieldId === row.fieldId,
        )
        // A concurrent insert of the same pair becomes a patch of the existing row (docs/03 §11.5).
        if (duplicate) {
          const patched = this.patch(
            user,
            { ...m, op: 'patch', id: duplicate.id, changes: { value: { old: null, new: row.value } } },
            duplicate,
          )
          if (patched.result.status !== 'accepted') return patched
          return { ...patched, result: { ...patched.result, id: m.id, mergedInto: duplicate.id } }
        }
      }
      if (samePair[m.table]) return this.insertPair(user, m, row)
      return this.accepted(m, this.write(m.table, row, user.id))
    }
    if (m.table === 'user_settings') {
      const duplicate = [...this.table('user_settings').values()].find(
        (s) => !s.deleted && s.userId === user.id && s.key === row.key,
      )
      // Two devices, one key: a patch of the value with old = the existing one – the later device wins.
      if (duplicate) {
        const merged = jsonEqual(duplicate.value, row.value)
          ? duplicate
          : this.write('user_settings', { ...duplicate, value: row.value }, user.id)
        return this.accepted(m, merged, { id: m.id, mergedInto: duplicate.id })
      }
      return this.accepted(m, this.write('user_settings', { ...row, userId: user.id }, user.id))
    }
    return this.accepted(m, this.write(m.table, { ...row, source: 'manual' }, user.id))
  }

  private remember(batch: string, id: string) {
    if (!this.created.has(batch)) this.created.set(batch, new Set())
    this.created.get(batch)!.add(id)
  }

  /** The status a new item gets when none is sent: the template's default, else the type's (not archived). */
  private defaultStatusOf(type: string, templateId: unknown): string | null {
    const template = templateId ? this.table('project_templates').get(templateId as string) : undefined
    if (type === 'project' && template?.defaultStatusId) return template.defaultStatusId as string
    const status = [...this.table('statuses').values()].find(
      (s) => s.itemType === type && s.isDefault === true && !s.deleted && !s.isArchived,
    )
    return status?.id ?? null
  }

  /** `statusId` and `imageFileId` as the server checks them (ItemsTable.ValidateAsync); null if fine. */
  private itemErrors(row: Row & { id: string }, keys: readonly string[] | null): Record<string, string[]> | null {
    const type = row.type as string
    if ((keys === null || keys.includes('statusId')) && row.statusId) {
      const status = this.table('statuses').get(row.statusId as string)
      if (!status || status.deleted || status.itemType !== type) return { statusId: [`A status of ${type}s.`] }
      if (status.isArchived) return { statusId: ['The status is archived.'] }
    }
    if ((keys === null || keys.includes('imageFileId')) && row.imageFileId) {
      const file = this.table('files').get(row.imageFileId as string)
      if (!file || file.deleted || file.itemId !== row.id || !String(file.contentType).startsWith('image/'))
        return { imageFileId: ['An image file of this item.'] }
    }
    return null
  }

  /**
   * A new item of any type (phase 3): a title, a status of its type for ideas, leads and projects (none sent: the
   * default), the per-type number, the owner; an origin (conversion) is an idea or lead the user reads – with edit on it,
   * its `convertedToItemId` is set in the same transaction (docs/03 §11.4; the client never patches it).
   */
  private insertItem(user: User, m: WireMutation, row: Row & { id: string }): Stored {
    const type = row.type as string
    if (!['note', 'idea', 'lead', 'project'].includes(type)) return this.rejected(m, 'not_supported')
    if (typeof row.title !== 'string' || !row.title.trim() || row.title.length > 300)
      return this.rejected(m, 'validation_failed', null, { title: ['1 to 300 characters.'] })
    if (type !== 'note' && !row.statusId) row.statusId = this.defaultStatusOf(type, row.projectTemplateId)
    if (type === 'note' ? !!row.statusId : !row.statusId)
      return this.rejected(m, 'validation_failed', null, {
        statusId: [type === 'note' ? 'Notes have no status.' : 'A status is required.'],
      })
    if (type !== 'note' && row.pinned === true)
      return this.rejected(m, 'validation_failed', null, { pinned: ['Only notes are pinned.'] })
    const errors = this.itemErrors(row, null)
    if (errors) return this.rejected(m, 'validation_failed', null, errors)
    let origin: ServerRow | undefined
    if (row.originItemId) {
      origin = this.table('items').get(row.originItemId as string)
      if (
        type !== 'project' ||
        !origin ||
        origin.deleted ||
        !['idea', 'lead'].includes(origin.type as string) ||
        !this.itemAccess(user, origin)
      )
        return this.rejected(m, 'validation_failed', null, {
          originItemId: ['A project’s origin is an idea or a lead you can read.'],
        })
    }
    const number = (this.numbers.get(type) ?? 0) + 1
    this.numbers.set(type, number)
    const stored = this.write(
      'items',
      {
        ...row,
        ownerUserId: user.id,
        number,
        statusChangedAt: row.statusId ? this.now() : null,
        convertedToItemId: null,
      },
      user.id,
    )
    const originAccess = origin ? this.itemAccess(user, origin) : null
    if (origin && originAccess && accessRank[originAccess] >= accessRank.edit)
      this.write('items', { ...origin, convertedToItemId: row.id }, user.id)
    this.remember(m.batch, m.id)
    return this.accepted(m, stored)
  }

  /** A gate's decision stamps (MilestonesTable): who decided when, for a decision other than pending. */
  private decided(user: User, row: Row) {
    const decided = !!row.decision && row.decision !== 'pending'
    row.decidedAt = decided ? this.now() : null
    row.decidedBy = decided ? user.id : null
  }

  /** An insert of a unique pair: a duplicate is merged into the existing row (`mergedInto`, docs/03 §11.5). */
  private insertPair(user: User, m: WireMutation, row: Row & { id: string }): Stored {
    const keys = samePair[m.table] ?? []
    const duplicate = [...this.table(m.table).values()].find(
      (r) => !r.deleted && keys.every((k) => (r[k] ?? null) === (row[k] ?? null)),
    )
    // `id` stays the client's, `mergedInto` is the row it became.
    if (duplicate) return this.accepted(m, duplicate, { id: m.id, mergedInto: duplicate.id })
    return this.accepted(m, this.write(m.table, row, user.id))
  }

  private patch(user: User, m: WireMutation, row: ServerRow): Stored {
    if (row.deleted) return this.conflict(m, row, 'deleted_by_other', this.currentRow(user, m.table, row))
    const changes = m.changes ?? {}
    const keys = Object.keys(changes)
    if (keys.some((k) => !isPatchable(m.table, k)))
      return this.rejected(m, 'not_patchable', this.currentRow(user, m.table, row))
    // A Microsoft person: notes only; a user's own person: not the name and e-mail (they follow the user).
    if (
      m.table === 'persons' &&
      (row.source === 'microsoft'
        ? keys.some((k) => k !== 'notes')
        : !!row.userId && keys.some((k) => k === 'displayName' || k === 'email'))
    )
      return this.rejected(m, 'not_patchable', this.currentRow(user, m.table, row))
    if (m.table === 'saved_views') {
      // A shared view (or sharing one) needs Configuration: Manage; a personal one is its owner's.
      if (row.isShared === true || keys.includes('isShared')) {
        if (!user.configManage) return this.rejected(m, 'missing_right', this.currentRow(user, m.table, row))
      } else if (row.ownerUserId !== user.id) {
        return this.rejected(m, 'missing_access', this.currentRow(user, m.table, row))
      }
    } else if (!this.canWrite(user, m.table, row, 'edit'))
      return this.rejected(m, 'missing_access', this.currentRow(user, m.table, row))
    if (m.table === 'items') {
      const next = { ...row, ...Object.fromEntries(keys.map((k) => [k, changes[k]!.new])) }
      const errors = this.itemErrors(next, keys)
      if (errors) return this.rejected(m, 'validation_failed', this.currentRow(user, m.table, row), errors)
    }
    const apply: Record<string, { old: unknown; new: unknown }> = {}
    const conflicts: FieldConflict[] = []
    for (const [key, change] of Object.entries(changes)) {
      const current = getPath(row, key)
      if (jsonEqual(current, change.old)) apply[key] = change
      else if (!jsonEqual(current, change.new)) {
        const last = this.lastChange.get(`${m.table}:${row.id}:${key}`)
        conflicts.push({
          mutationId: m.mutationId,
          table: m.table,
          id: row.id,
          field: key,
          theirs: current ?? null,
          by: last?.by ?? null,
          at: last?.at ?? null,
          rev: row.rev,
        })
      }
    }
    let stored = row
    if (Object.keys(apply).length) {
      const next = applyPatch(row, apply) as ServerRow
      // The server ages the status (data model §7.1) and stamps milestones and DoD items (docs/03 §15 #50).
      if (m.table === 'items' && apply.statusId) next.statusChangedAt = this.now()
      if (m.table === 'milestones' && apply.status)
        next.completedAt = next.status === 'done' ? (next.completedAt ?? this.now()) : null
      if (m.table === 'milestones' && apply.decision) this.decided(user, next)
      if (m.table === 'dod_items' && apply.state) {
        next.stateChangedAt = this.now()
        next.stateChangedBy = user.id
      }
      stored = this.write(m.table, next, user.id)
      for (const key of Object.keys(apply)) {
        this.lastChange.set(`${m.table}:${row.id}:${key}`, { by: user.name, at: stored.updatedAt as string })
      }
    }
    if (!conflicts.length) return this.accepted(m, stored)
    const conflictFields = conflicts.map((c) => c.field)
    if (!Object.keys(apply).length) {
      // Every key conflicted (or was a no-op): nothing applied, the row as it is.
      return {
        ...this.conflict(m, stored, 'field', this.currentRow(user, m.table, stored), { conflictFields }),
        conflicts,
      }
    }
    return {
      result: {
        mutationId: m.mutationId,
        status: 'partial',
        id: row.id,
        rev: stored.rev,
        seq: stored.seq,
        conflictFields,
      },
      conflicts,
    }
  }

  private remove(user: User, m: WireMutation, row: ServerRow): Stored {
    if (row.deleted) return this.accepted(m, row)
    if (['item_shares', 'map_shares', 'user_settings'].includes(m.table)) return this.rejected(m, 'not_supported')
    if (m.table === 'saved_views' && row.isShared === true) {
      if (!user.configManage) return this.rejected(m, 'missing_right', this.currentRow(user, m.table, row))
    } else if (!this.canWrite(user, m.table, row, 'manage'))
      return this.rejected(m, 'missing_access', this.currentRow(user, m.table, row))
    if (!m.force && m.baseRev !== null && row.rev !== m.baseRev) {
      return this.conflict(m, row, 'changed_since_delete', this.currentRow(user, m.table, row))
    }
    const batch = uuidv7()
    // The item's shares stay out of its batch: they decide who sees the tombstone and may restore it.
    if (m.table === 'items') {
      this.batches.set(batch, { rootKind: 'item', rootId: row.id, at: this.now(), by: user.id, restored: false })
    }
    const deleted = this.write(m.table, { ...row, deleted: true, deleteBatchId: batch }, user.id)
    this.cascade(m.table, row.id, batch, user.id)
    return this.accepted(m, deleted)
  }

  /** What is deleted with a row, in its batch (docs/01 §4.9): an item's children and the links to it. */
  private cascade(table: string, id: string, batch: string, by: string) {
    const gone = (child: string, test: (r: ServerRow) => boolean) => {
      for (const r of [...this.table(child).values()]) {
        if (r.deleted || !test(r)) continue
        this.write(child, { ...r, deleted: true, deleteBatchId: batch }, by)
        this.cascade(child, r.id, batch, by)
      }
    }
    if (table === 'items') {
      for (const child of itemChildren) gone(child, (r) => r.itemId === id)
      gone('item_links', (r) => r.fromItemId === id || r.toItemId === id)
      gone('map_elements', (r) => r.itemId === id)
    } else if (table === 'milestones') {
      gone('dod_items', (r) => r.milestoneId === id)
      gone('assignments', (r) => r.milestoneId === id)
    } else if (table === 'maps') {
      for (const child of ['map_layers', 'map_elements', 'files']) gone(child, (r) => r.mapId === id)
    }
  }

  // --- REST -----------------------------------------------------------------------------------------------------

  /** A change by someone on the server (another client, MCP, an admin): stamped, change-logged, published. */
  change(userId: string, table: string, id: string, values: Record<string, unknown>) {
    const user = this.user(userId)
    const row = this.table(table).get(id)!
    const stored = this.write(table, { ...row, ...values }, userId)
    for (const key of Object.keys(values))
      this.lastChange.set(`${table}:${id}:${key}`, { by: user.name, at: stored.updatedAt as string })
    this.publish()
  }

  /** `PUT /api/v1/items/{id}/shares`: diff, re-stamp the subtree, evictions for those who lost access; the new list. */
  putShares(userId: string, itemId: string, shares: ShareSpec[]): ItemShare[] {
    const user = this.user(userId)
    const item = this.table('items').get(itemId)
    if (!item || this.itemAccess(user, item) !== 'manage') throw new ApiError(403, { code: 'missing_access' })
    const before = new Map([...this.users.values()].map((u) => [u.id, this.itemAccess(u, item)]))
    const current = [...this.table('item_shares').values()].filter((s) => s.itemId === itemId && !s.deleted)
    for (const share of current) {
      const wanted = shares.find((s) => s.groupId === share.groupId)
      if (!wanted) this.write('item_shares', { ...share, deleted: true, deleteBatchId: uuidv7() }, userId)
      else if (wanted.access !== share.access) this.write('item_shares', { ...share, access: wanted.access }, userId)
    }
    for (const wanted of shares) {
      if (!current.some((s) => s.groupId === wanted.groupId)) {
        this.write('item_shares', { id: uuidv7(), itemId, groupId: wanted.groupId, access: wanted.access }, userId)
      }
    }
    this.restamp('items', itemId)
    for (const share of this.table('item_shares').values())
      if (share.itemId === itemId) this.restamp('item_shares', share.id)
    for (const other of this.users.values()) {
      if (before.get(other.id) && !this.itemAccess(other, item)) {
        this.evictions.push({ userId: other.id, seq: ++this.seq, kind: 'item', id: itemId })
      }
    }
    this.publish()
    return [...this.table('item_shares').values()]
      .filter((s) => s.itemId === itemId && !s.deleted)
      .map((s) => ({
        id: s.id,
        groupId: s.groupId as string,
        groupName: String(this.table('groups').get(s.groupId as string)?.name ?? ''),
        access: s.access as Access,
      }))
  }

  /** `GET /api/v1/trash`: the batches of roots the user may manage. */
  trash(userId: string): TrashEntry[] {
    const user = this.user(userId)
    return [...this.batches.entries()]
      .filter(([, b]) => !b.restored)
      .filter(([, b]) => {
        const item = this.table('items').get(b.rootId)
        return !!item && this.itemAccess(user, item) === 'manage'
      })
      .map(([batchId, b]) => ({
        batchId,
        rootKind: b.rootKind,
        rootId: b.rootId,
        rootType: String(this.table('items').get(b.rootId)?.type ?? 'note'),
        title: String(this.table('items').get(b.rootId)?.title ?? ''),
        deletedAt: b.at,
        deletedBy: b.by,
        deletedByName: this.users.get(b.by)?.name ?? null,
      }))
  }

  /** `POST /api/v1/trash/{batchId}/restore`: the batch's rows again, with new seqs. */
  restore(userId: string, batchId: string): { batchId: string; restored: number; skipped: number } {
    const batch = this.batches.get(batchId)
    if (!batch || batch.restored) throw new ApiError(404, { code: 'not_found' })
    let restored = 0
    for (const table of tableNames) {
      for (const row of this.table(table).values()) {
        if (row.deleteBatchId === batchId) {
          this.write(table, { ...row, deleted: false, deleteBatchId: null }, userId)
          restored++
        }
      }
    }
    batch.restored = true
    this.publish()
    return { batchId, restored, skipped: 0 }
  }

  // --- files (docs/03 §10.3, PGPM/Files/FileService.cs) -----------------------------------------------------------

  /** The files row a transfer is for: 404 when it doesn't exist or is hidden, 403 `missing_access` below the level. */
  private fileFor(userId: string, id: string, level: 'read' | 'edit'): ServerRow {
    const user = this.user(userId)
    const row = this.table('files').get(id)
    if (!row || row.deleted || !this.visible(user, 'files', row))
      throw new ApiError(404, { code: 'not_found', detail: 'File not found.' })
    if (level === 'edit' && !this.canWrite(user, 'files', row, 'edit'))
      throw new ApiError(403, { code: 'missing_access', detail: 'You need edit access to the file’s item or map.' })
    return row
  }

  /**
   * A new `files` row (FilesTable): either an item or a map, image purposes PNG/JPEG/GIF/WebP, no blocked name, at most
   * the purpose's limit; `pending` – unless the same bytes (SHA-256 and size) are stored already for a file the user can
   * read: then it is `available` at once and shares them (no upload, docs/03 §10.3).
   */
  private insertFile(user: User, m: WireMutation, row: Row & { id: string }): Stored {
    const image = imagePurposes.includes(row.purpose as string)
    const errors: Record<string, string[]> = {}
    if (row.purpose === 'map_image' ? !row.mapId : !row.itemId)
      errors.purpose = ['Map images belong to maps; covers, attachments and embedded images to items.']
    const name = String(row.fileName ?? '')
    if (!name.trim() || name.length > 255 || /[/\\]/.test(name)) errors.fileName = ['1 to 255 characters, no path.']
    else if (blockedIn(name)) errors.fileName = [`Files ending in .${blockedIn(name)} can't be stored.`]
    if (image && !imageExtensions[String(row.contentType)]) errors.contentType = ['Images are PNG, JPEG, GIF or WebP.']
    if (!/^[0-9a-f]{64}$/i.test(String(row.sha256 ?? ''))) errors.sha256 = ['The SHA-256 of the bytes, hex.']
    const size = Number(row.sizeBytes)
    if (!(size > 0)) errors.sizeBytes = ['More than 0 bytes.']
    else if (size > (image ? maxImage : maxAttachment)) errors.sizeBytes = ['Too large for this purpose.']
    if (typeof row.thumbnail === 'string' && row.thumbnail.length > 32 * 1024) errors.thumbnail = ['At most 32 KB.']
    if (Object.keys(errors).length) return this.rejected(m, 'validation_failed', null, errors)
    const sha256 = String(row.sha256).toLowerCase()
    const shared = [...this.table('files').values()]
      .sort((a, b) => b.seq - a.seq)
      .find(
        (f) =>
          f.sha256 === sha256 &&
          f.sizeBytes === size &&
          f.status === 'available' &&
          !f.deleted &&
          this.fileBytes.has(f.id) &&
          this.visible(user, 'files', f) &&
          (!image || String(f.contentType).startsWith('image/')),
      )
    if (shared) this.fileBytes.set(row.id, this.fileBytes.get(shared.id)!)
    const values: Row = shared
      ? { status: 'available', contentType: shared.contentType, width: shared.width, height: shared.height }
      : { status: 'pending' }
    return this.accepted(m, this.write('files', { ...row, sha256, ...values }, user.id))
  }

  /**
   * `PUT /api/v1/files/{id}/chunks/{n}`: chunk 0 starts (over), every other must be the next (409 `chunk_out_of_order`
   * with `expected`); 409 `file_available` when stored already; 413 `too_large` for a chunk over 4 MiB or more bytes
   * than the row's size.
   */
  putChunk(userId: string, id: string, index: number, bytes: Uint8Array): { received: number } {
    if (!this.online) throw new ApiError(0, undefined)
    const row = this.fileFor(userId, id, 'edit')
    if (row.status === 'available')
      throw new ApiError(409, { code: 'file_available', detail: 'The file’s bytes are stored already.' })
    if (bytes.length > chunkBytes) throw new ApiError(413, { code: 'too_large', detail: 'A chunk is at most 4 MiB.' })
    if (index === 0) this.uploads.set(id, [])
    const parts = this.uploads.get(id)
    if (!parts || parts.length !== index)
      throw new ApiError(409, {
        code: 'chunk_out_of_order',
        detail: 'Start the upload again with chunk 0.',
        expected: parts?.length ?? 0,
      })
    parts.push(bytes.slice())
    const received = parts.reduce((n, p) => n + p.length, 0)
    if (received > Number(row.sizeBytes)) {
      this.uploads.delete(id)
      throw new ApiError(413, { code: 'too_large', detail: 'More bytes than the file has.' })
    }
    return { received }
  }

  /** The commit's answer: the stored file (FileCommitted). */
  private committed(row: ServerRow): Row {
    const { id, status, sha256, sizeBytes, contentType, width, height, fileName, rev, seq } = row
    return { id, status, sha256, sizeBytes, contentType, width, height, fileName, rev, seq }
  }

  /**
   * `POST /api/v1/files/{id}/commit`: size and SHA-256 as received and as the row says (409 `size_mismatch` /
   * `sha256_mismatch`, the upload discarded; 409 `upload_missing` without one), a blocked name 422 `blocked_type`; an image
   * checked by its bytes (415 `not_an_image`) and its metadata stripped (here: PNG's text and time chunks) – the stored
   * hash, size, type, pixel size and a name with the type's extension go into the row (new rev) and the answer. A commit of
   * a stored file answers with it (a retry).
   */
  async commit(userId: string, id: string, body: { sha256: string; size: number }): Promise<Row> {
    if (!this.online) throw new ApiError(0, undefined)
    const errors: Record<string, string[]> = {}
    if (!/^[0-9a-f]{64}$/i.test(String(body.sha256 ?? ''))) errors.sha256 = ['The SHA-256 of the bytes, hex.']
    if (!(Number(body.size) > 0)) errors.size = ['The size in bytes.']
    if (Object.keys(errors).length) throw new ApiError(400, { code: 'validation_failed', errors })
    const row = this.fileFor(userId, id, 'edit')
    if (row.status === 'available') return this.committed(row)
    const parts = this.uploads.get(id)
    if (!parts)
      throw new ApiError(409, { code: 'upload_missing', detail: 'No upload of this file: send its chunks first.' })
    const bytes = new Uint8Array(parts.reduce((n, p) => n + p.length, 0))
    let at = 0
    for (const p of parts) {
      bytes.set(p, at)
      at += p.length
    }
    if (bytes.length !== body.size || body.size !== row.sizeBytes) {
      this.uploads.delete(id)
      throw new ApiError(409, { code: 'size_mismatch', detail: `Received ${bytes.length} bytes.` })
    }
    const hash = await sha256Hex(bytes)
    if (hash !== body.sha256.toLowerCase() || hash !== row.sha256) {
      this.uploads.delete(id)
      throw new ApiError(409, {
        code: 'sha256_mismatch',
        detail: 'The bytes received have another SHA-256 than the commit or the file says.',
      })
    }
    const blocked = blockedIn(String(row.fileName))
    if (blocked)
      throw new ApiError(422, { code: 'blocked_type', detail: `Files ending in .${blocked} can't be stored.` })
    let stored: Uint8Array = bytes
    const values: Row = {}
    if (imagePurposes.includes(row.purpose as string)) {
      const check = checkImage(bytes)
      if (!check) throw new ApiError(415, { code: 'not_an_image', detail: 'Not a PNG, JPEG, GIF or WebP image.' })
      stored = check.bytes
      values.contentType = check.contentType
      if (check.width) values.width = check.width
      if (check.height) values.height = check.height
      values.fileName = imageName(String(row.fileName), imageExtensions[check.contentType]!)
    }
    this.uploads.delete(id)
    this.fileBytes.set(id, { bytes: stored, type: String(values.contentType ?? row.contentType) })
    const written = this.write(
      'files',
      { ...row, ...values, status: 'available', sha256: await sha256Hex(stored), sizeBytes: stored.length },
      userId,
    )
    this.publish()
    return this.committed(written)
  }

  /** `GET /api/v1/files/{id}/{name}`: read access; 404 `file_pending` before the commit. */
  download(userId: string, id: string): { bytes: Uint8Array; type: string } {
    if (!this.online) throw new ApiError(0, undefined)
    const row = this.fileFor(userId, id, 'read')
    if (row.status !== 'available')
      throw new ApiError(404, { code: 'file_pending', detail: 'The file’s bytes haven’t been uploaded yet.' })
    const stored = this.fileBytes.get(id)
    if (!stored) throw new ApiError(404, { code: 'not_found', detail: 'File not found.' })
    // Images inline as their type, everything else as bytes to save.
    const image = imagePurposes.includes(row.purpose as string) && !!imageExtensions[String(row.contentType)]
    return { bytes: stored.bytes, type: image ? String(row.contentType) : 'application/octet-stream' }
  }

  // --- statuses (Configuration: Manage; PGPM/Config/ConfigService.cs) ----------------------------------------------

  private configUser(userId: string): User {
    const user = this.user(userId)
    if (!user.configManage && !user.isAdmin)
      throw new ApiError(403, { code: 'missing_right', detail: 'You need Configuration: Manage.' })
    return user
  }

  /** `StatusItem`: what the endpoints answer. */
  private statusItem(row: ServerRow): Row {
    const { id, itemType, name, color, icon, category, sortOrder, isDefault, maxDays, isArchived, rev } = row
    return { id, itemType, name, color, icon, category, sortOrder, isDefault, maxDays, isArchived, rev }
  }

  /** The request checked as the server does: 400 `validation_failed` with `errors` by field. */
  private statusInput(body: Row): Row {
    const errors: Record<string, string[]> = {}
    const name = typeof body.name === 'string' ? body.name.trim() : ''
    if (name.length < 1 || name.length > 100) errors.name = ['1 to 100 characters.']
    const color = typeof body.color === 'string' ? body.color.toLowerCase() : ''
    if (!/^#[0-9a-f]{6}$/.test(color)) errors.color = ['A colour #rrggbb.']
    const icon = typeof body.icon === 'string' && body.icon.trim() ? body.icon.trim() : null
    if (icon !== null && !/^pi-[a-z0-9-]+$/.test(icon)) errors.icon = ['A PrimeIcons name (pi-lightbulb), or none.']
    if (!['open', 'active', 'closed'].includes(body.category as string)) errors.category = ['open, active or closed.']
    const maxDays = body.maxDays ?? null
    if (maxDays !== null && !(Number.isInteger(maxDays) && (maxDays as number) >= 1 && (maxDays as number) <= 36500))
      errors.maxDays = ['1 to 36,500 days, or none.']
    if (Object.keys(errors).length) throw new ApiError(400, { code: 'validation_failed', errors })
    return { name, color, icon, category: body.category, maxDays }
  }

  private statusesOf(itemType: unknown): ServerRow[] {
    return [...this.table('statuses').values()].filter((s) => s.itemType === itemType && !s.deleted)
  }

  /** 409 `duplicate_name`: another live, not archived status of the type has the name (ignoring case). */
  private uniqueStatusName(itemType: unknown, name: string, except: string | null) {
    const taken = this.statusesOf(itemType).some(
      (s) => !s.isArchived && s.id !== except && String(s.name).toLowerCase() === name.toLowerCase(),
    )
    if (taken)
      throw new ApiError(409, { code: 'duplicate_name', detail: `Another ${String(itemType)} status has this name.` })
  }

  /** Exactly one default per type: setting one clears the others. */
  private onlyDefault(itemType: unknown, id: string, by: string) {
    for (const s of this.statusesOf(itemType)) {
      if (s.id !== id && s.isDefault) this.write('statuses', { ...s, isDefault: false }, by)
    }
  }

  /** `POST /api/v1/config/statuses`: the first of a type, or `isDefault`, is the default; sort order last + 10. */
  createStatus(userId: string, body: Row): Row {
    this.configUser(userId)
    if (!['idea', 'lead', 'project'].includes(body.itemType as string))
      throw new ApiError(400, { code: 'validation_failed', errors: { itemType: ['idea, lead or project.'] } })
    const input = this.statusInput(body)
    this.uniqueStatusName(body.itemType, input.name as string, null)
    const same = this.statusesOf(body.itemType)
    const isDefault = body.isDefault === true || !same.some((s) => s.isDefault)
    const id = uuidv7()
    const stored = this.write(
      'statuses',
      {
        id,
        itemType: body.itemType,
        ...input,
        isDefault,
        isArchived: false,
        sortOrder: Math.max(0, ...same.map((s) => Number(s.sortOrder))) + 10,
      },
      userId,
    )
    if (isDefault) this.onlyDefault(body.itemType, id, userId)
    this.publish()
    return this.statusItem(stored)
  }

  /** `PUT /api/v1/config/statuses/{id}`: 409 `default_required` to undo the default, `status_archived` to make one. */
  updateStatus(userId: string, id: string, body: Row): Row {
    this.configUser(userId)
    const input = this.statusInput(body)
    const row = this.table('statuses').get(id)
    if (!row || row.deleted) throw new ApiError(404, { code: 'not_found', detail: 'Status not found.' })
    if (body.itemType != null && body.itemType !== row.itemType)
      throw new ApiError(400, {
        code: 'validation_failed',
        errors: { itemType: ['A status’s item type doesn’t change.'] },
      })
    if (!row.isArchived) this.uniqueStatusName(row.itemType, input.name as string, id)
    if (row.isDefault && body.isDefault === false)
      throw new ApiError(409, { code: 'default_required', detail: 'Make another status the default instead.' })
    const makeDefault = body.isDefault === true && !row.isDefault
    if (makeDefault && row.isArchived)
      throw new ApiError(409, { code: 'status_archived', detail: 'An archived status can’t be the default.' })
    if (makeDefault) this.onlyDefault(row.itemType, id, userId)
    const stored = this.write('statuses', { ...row, ...input, isDefault: row.isDefault || makeDefault }, userId)
    this.publish()
    return this.statusItem(stored)
  }

  /** `POST …/{id}/archive` · `/unarchive`: never the default (409 `default_status`). */
  archiveStatus(userId: string, id: string, archived: boolean): Row {
    this.configUser(userId)
    const row = this.table('statuses').get(id)
    if (!row || row.deleted) throw new ApiError(404, { code: 'not_found', detail: 'Status not found.' })
    if (archived && row.isDefault)
      throw new ApiError(409, {
        code: 'default_status',
        detail: 'The default status can’t be archived: make another one the default first.',
      })
    if (!archived && row.isArchived) this.uniqueStatusName(row.itemType, String(row.name), id)
    const stored = this.write('statuses', { ...row, isArchived: archived }, userId)
    this.publish()
    return this.statusItem(stored)
  }

  /** `PUT …/order {itemType, ids}`: every id of the type once; sort orders 10, 20, …; the type's statuses in order. */
  orderStatuses(userId: string, itemType: string, ids: string[]): Row[] {
    this.configUser(userId)
    const statuses = this.statusesOf(itemType)
    if (
      ids.length !== statuses.length ||
      new Set(ids).size !== ids.length ||
      !ids.every((i) => statuses.some((s) => s.id === i))
    )
      throw new ApiError(400, { code: 'validation_failed', errors: { ids: ['Every id of the list, once.'] } })
    ids.forEach((id, i) => {
      const row = this.table('statuses').get(id)!
      if (row.sortOrder !== (i + 1) * 10) this.write('statuses', { ...row, sortOrder: (i + 1) * 10 }, userId)
    })
    this.publish()
    return ids.map((id) => this.statusItem(this.table('statuses').get(id)!))
  }

  /**
   * The REST endpoints above as a `fetch` for a user (tests stub `globalThis.fetch` with it): files and statuses.
   * Anything else answers 404. Offline: the fetch fails as a real one does (TypeError).
   */
  fetchFor(userId: string): typeof fetch {
    return async (input, init) => {
      const request = input instanceof Request ? input : new Request(String(input), init)
      const url = new URL(request.url)
      const method = request.method.toUpperCase()
      this.calls.push(`${method} ${url.pathname}`)
      if (this.latency) await new Promise((r) => setTimeout(r, this.latency))
      if (!this.online) throw new TypeError('Failed to fetch')
      const json = (status: number, body?: unknown) =>
        new Response(body === undefined ? null : JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        })
      try {
        const path = url.pathname
        let m: RegExpExecArray | null
        if ((m = /^\/api\/v1\/files\/([^/]+)\/chunks\/(\d+)$/.exec(path)) && method === 'PUT') {
          const bytes = new Uint8Array(await request.arrayBuffer())
          return json(200, this.putChunk(userId, m[1]!, Number(m[2]), bytes))
        }
        if ((m = /^\/api\/v1\/files\/([^/]+)\/commit$/.exec(path)) && method === 'POST') {
          return json(200, await this.commit(userId, m[1]!, (await request.json()) as { sha256: string; size: number }))
        }
        if ((m = /^\/api\/v1\/files\/([^/]+)\/[^/]+$/.exec(path)) && method === 'GET') {
          const file = this.download(userId, m[1]!)
          return new Response(file.bytes.slice().buffer, {
            status: 200,
            headers: { 'Content-Type': file.type, ETag: `"${String(this.table('files').get(m[1]!)?.sha256)}"` },
          })
        }
        if (path === '/api/v1/config/statuses' && method === 'POST') {
          return json(200, this.createStatus(userId, (await request.json()) as Row))
        }
        if (path === '/api/v1/config/statuses/order' && method === 'PUT') {
          const body = (await request.json()) as { itemType: string; ids: string[] }
          return json(200, this.orderStatuses(userId, body.itemType, body.ids))
        }
        if ((m = /^\/api\/v1\/config\/statuses\/([^/]+)\/(archive|unarchive)$/.exec(path)) && method === 'POST') {
          return json(200, this.archiveStatus(userId, m[1]!, m[2] === 'archive'))
        }
        if ((m = /^\/api\/v1\/config\/statuses\/([^/]+)$/.exec(path)) && method === 'PUT') {
          return json(200, this.updateStatus(userId, m[1]!, (await request.json()) as Row))
        }
        return json(404, { code: 'not_found' })
      } catch (e) {
        if (e instanceof ApiError) {
          if (e.status === 0) throw new TypeError('Failed to fetch')
          return json(e.status, { ...e.body, code: e.code })
        }
        throw e
      }
    }
  }

  /** Group membership changed (an admin): the access epoch goes up; live connections get `reset`, then close 4409. */
  setGroups(userId: string, groupIds: string[]) {
    const user = this.user(userId)
    user.groupIds = groupIds
    user.accessEpoch++
    for (const c of [...this.connections]) if (c.userId === userId) c.reset()
  }

  /** Signed out everywhere (`revoked`) or the 30 days over (`expired`): `session`, then close 4401. */
  endSession(userId: string, state: 'revoked' | 'expired') {
    for (const c of [...this.connections]) {
      if (c.userId !== userId) continue
      c.send({ type: 'session', state })
      c.drop(4401)
    }
  }

  /** The server's heartbeat (every 25 s): a `ping` to every connection. */
  ping() {
    for (const c of this.connections) c.send({ type: 'ping' })
  }

  /** `POST /api/v1/editing`: the others who may read the item hear it; never the editor's own user. */
  startEditing(userId: string, itemId: string, target: string) {
    const user = this.user(userId)
    const message: EditingMessage = { type: 'editing', itemId, target, userId, user: user.name, since: this.now() }
    this.endEditing(userId, itemId, target, false)
    this.editing.push(message)
    this.tellEditing(message)
  }

  /** `DELETE /api/v1/editing`. */
  endEditing(userId: string, itemId: string, target: string, tell = true) {
    const at = this.editing.findIndex((e) => e.userId === userId && e.itemId === itemId && e.target === target)
    if (at < 0) return
    const [open] = this.editing.splice(at, 1)
    if (tell) this.tellEditing({ type: 'editing_end', itemId, target, userId, user: open!.user })
  }

  /** Whether the user may read the item now (the editing notices go to readers only). */
  canRead(userId: string, itemId: string): boolean {
    const user = this.users.get(userId)
    const item = this.table('items').get(itemId)
    return !!user && !!item && this.itemAccess(user, item) !== null
  }

  private tellEditing(message: EditingMessage) {
    for (const c of this.connections) {
      if (c.welcomed && c.userId !== message.userId && this.canRead(c.userId, message.itemId)) c.send({ ...message })
    }
  }

  /** The network goes down: requests fail, sockets drop. */
  goOffline() {
    this.online = false
    for (const c of [...this.connections]) c.drop(1006)
  }

  goOnline() {
    this.online = true
  }

  // --- live -----------------------------------------------------------------------------------------------------

  /** A client's WebSocket to `/api/v1/sync/live`; `reachable` false: that client's network is down. */
  connect(userId: string, reachable = true): FakeWebSocket {
    return new FakeWebSocket(this, userId, reachable)
  }

  /** After every commit (ChangeFeed.Publish): the connections behind the head get their next batch. */
  publish() {
    for (const c of this.connections) c.pump()
  }

  /** Closes the user's connections with a code (4401, 4409 …). */
  close(userId: string, code: number) {
    for (const c of [...this.connections]) if (c.userId === userId) c.drop(code)
  }
}

export class ServerConnection {
  cursor = 0
  welcomed = false
  awaiting = false
  /** The cursor of the batch waiting for its ack. */
  sentCursor = 0
  pongs = 0
  readonly sent: Record<string, unknown>[] = []
  readonly server: FakeSyncServer
  readonly userId: string
  readonly socket: FakeWebSocket

  constructor(server: FakeSyncServer, userId: string, socket: FakeWebSocket) {
    this.server = server
    this.userId = userId
    this.socket = socket
  }

  send(message: Record<string, unknown>) {
    this.sent.push(message)
    this.socket.deliver(JSON.stringify(message))
  }

  drop(code: number) {
    this.server.connections.delete(this)
    this.socket.serverClosed(code)
  }

  /** `reset`, then close 4409. */
  reset() {
    this.send({ type: 'reset' })
    this.drop(4409)
  }

  receive(message: Record<string, unknown>) {
    const user = this.server.users.get(this.userId)!
    switch (message.type) {
      case 'hello': {
        if (this.welcomed) return
        const since = message.since as number
        if (
          since < 0 ||
          (since > 0 &&
            (message.accessEpoch !== user.accessEpoch || since < this.server.horizon || since > this.server.seq))
        ) {
          this.reset()
          return
        }
        this.cursor = since
        this.welcomed = true
        this.send({ type: 'welcome', head: this.server.seq })
        for (const notice of this.server.editing) {
          if (notice.userId !== this.userId && this.server.canRead(this.userId, notice.itemId)) this.send({ ...notice })
        }
        this.pump()
        break
      }
      case 'ack':
        // Applied up to at least what was sent: the next batch. Anything else is ignored.
        if (this.awaiting && typeof message.cursor === 'number' && message.cursor >= this.sentCursor) {
          this.cursor = Math.max(this.cursor, this.sentCursor)
          this.awaiting = false
          this.pump()
        }
        break
      case 'ping':
        this.send({ type: 'pong' })
        break
      case 'pong':
        this.pongs++
        break
      default:
        break
    }
  }

  /** Flow control: never more than one batch without an ack. */
  pump() {
    if (!this.welcomed || this.awaiting || this.cursor >= this.server.seq) return
    const page = this.server.pull(this.userId, this.cursor, 500)
    if (!Object.keys(page.changes).length && !page.evictions.length) {
      this.cursor = Math.max(this.cursor, page.cursor)
      return
    }
    this.awaiting = true
    this.sentCursor = page.cursor
    this.send({ type: 'changes', ...page })
  }
}

/** The client side of a fake WebSocket: asynchronous like a real one. */
export class FakeWebSocket implements SocketLike {
  readyState = 0
  onopen: ((event: unknown) => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: ((event: { code: number; reason?: string }) => void) | null = null
  onerror: ((event: unknown) => void) | null = null
  private readonly connection: ServerConnection | null = null
  private readonly server: FakeSyncServer

  constructor(server: FakeSyncServer, userId: string, reachable = true) {
    this.server = server
    if (!server.online || !reachable) {
      setTimeout(() => this.serverClosed(1006), 0)
      return
    }
    this.connection = new ServerConnection(server, userId, this)
    server.connections.add(this.connection)
    setTimeout(() => {
      if (this.readyState !== 0) return
      this.readyState = 1
      this.onopen?.({})
    }, 0)
  }

  send(data: string) {
    if (this.readyState !== 1 || !this.connection) return
    const message = JSON.parse(data) as Record<string, unknown>
    setTimeout(() => {
      if (this.server.connections.has(this.connection!)) this.connection!.receive(message)
    }, 0)
  }

  close(code = 1000) {
    if (this.readyState === 3) return
    this.readyState = 3
    if (this.connection) this.server.connections.delete(this.connection)
    setTimeout(() => this.onclose?.({ code }), 0)
  }

  deliver(data: string) {
    setTimeout(() => {
      if (this.readyState === 1) this.onmessage?.({ data })
    }, 0)
  }

  serverClosed(code: number) {
    if (this.readyState === 3) return
    this.readyState = 3
    setTimeout(() => this.onclose?.({ code }), 0)
  }
}
