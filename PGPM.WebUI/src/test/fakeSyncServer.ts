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
 * microseconds, as PostgreSQL keeps them. Checked against the real server by the e2e tests (e2e/sync.spec.ts).
 */

type ServerRow = Row & { id: string; seq: number; rev: number; deleted: boolean; deleteBatchId: string | null }

interface User {
  id: string
  name: string
  groupIds: string[]
  isAdmin: boolean
  accessEpoch: number
}

/** The patchable whitelist per table (docs/03 §11.5); others: read-only or insert-only. */
const patchable: Record<string, readonly string[]> = {
  items: ['title', 'description', 'color', 'imageFileId', 'statusId', 'pinned', 'tags'],
  persons: ['notes', 'displayName', 'email', 'jobTitle', 'department', 'companyName', 'phone', 'mobilePhone'],
  user_settings: ['value'],
}

const tableNames = ['items', 'item_shares', 'groups', 'persons', 'user_settings']

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

  addUser(user: { id: string; name: string; groupIds?: string[]; isAdmin?: boolean }): User {
    const full: User = { groupIds: [], isAdmin: false, accessEpoch: 1, ...user }
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

  private visible(user: User, table: string, row: ServerRow): boolean {
    switch (table) {
      case 'items':
        return this.itemAccess(user, row) !== null
      case 'item_shares': {
        const item = this.table('items').get(row.itemId as string)
        return !!item && this.itemAccess(user, item) !== null
      }
      case 'user_settings':
        return row.userId === user.id
      default:
        return true
    }
  }

  private wireRow(user: User, table: string, row: ServerRow): Row {
    if (row.deleted) return { id: row.id, seq: row.seq, rev: row.rev, deleted: true }
    const out: Row = clone(row)
    if (table === 'items') out.access = this.itemAccess(user, row)
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

  private canWrite(user: User, table: string, row: ServerRow, level: Access): boolean {
    if (table === 'items') {
      const access = this.itemAccess(user, row)
      return !!access && accessRank[access] >= accessRank[level]
    }
    if (table === 'user_settings') return row.userId === user.id
    return table === 'persons'
  }

  private apply(user: User, m: WireMutation): Stored {
    const rows = this.rows.get(m.table)
    if (!rows) return this.rejected(m, 'not_supported')
    if (m.table === 'groups') return this.rejected(m, m.op === 'patch' ? 'not_patchable' : 'not_supported')
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
    if (m.table === 'items') {
      // Computed per user: ignored as the server ignores unknown keys.
      delete row.access
      if (row.type !== 'note') return this.rejected(m, 'not_supported')
      if (typeof row.title !== 'string' || !row.title.trim())
        return this.rejected(m, 'validation_failed', null, { title: ['A title is needed.'] })
      const number = (this.numbers.get('note') ?? 0) + 1
      this.numbers.set('note', number)
      const stored = this.write('items', { ...row, ownerUserId: user.id, number }, user.id)
      if (!this.created.has(m.batch)) this.created.set(m.batch, new Set())
      this.created.get(m.batch)!.add(m.id)
      return this.accepted(m, stored)
    }
    if (m.table === 'item_shares') {
      const item = this.table('items').get(row.itemId as string)
      if (!item || item.deleted) return this.rejected(m, 'parent_missing')
      if (item.ownerUserId !== user.id || !this.created.get(m.batch)?.has(item.id)) {
        return this.rejected(m, 'missing_access')
      }
      const duplicate = [...this.table('item_shares').values()].find(
        (s) => !s.deleted && s.itemId === row.itemId && s.groupId === row.groupId,
      )
      // `id` stays the client's, `mergedInto` is the row it became.
      if (duplicate) return this.accepted(m, duplicate, { id: m.id, mergedInto: duplicate.id })
      return this.accepted(m, this.write('item_shares', row, user.id))
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

  private patch(user: User, m: WireMutation, row: ServerRow): Stored {
    if (row.deleted) return this.conflict(m, row, 'deleted_by_other', this.currentRow(user, m.table, row))
    const allowed = patchable[m.table] ?? []
    const changes = m.changes ?? {}
    if (Object.keys(changes).some((k) => !allowed.includes(k.split('.')[0]!)))
      return this.rejected(m, 'not_patchable', this.currentRow(user, m.table, row))
    if (!this.canWrite(user, m.table, row, 'edit'))
      return this.rejected(m, 'missing_access', this.currentRow(user, m.table, row))
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
      stored = this.write(m.table, applyPatch(row, apply) as ServerRow, user.id)
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
    if (m.table === 'item_shares' || m.table === 'user_settings') return this.rejected(m, 'not_supported')
    if (!this.canWrite(user, m.table, row, 'manage'))
      return this.rejected(m, 'missing_access', this.currentRow(user, m.table, row))
    if (!m.force && m.baseRev !== null && row.rev !== m.baseRev) {
      return this.conflict(m, row, 'changed_since_delete', this.currentRow(user, m.table, row))
    }
    const batch = uuidv7()
    // The item's shares stay out of its batch: they decide who sees the tombstone and may restore it.
    if (m.table === 'items') {
      this.batches.set(batch, { rootKind: 'item', rootId: row.id, at: this.now(), by: user.id, restored: false })
    }
    return this.accepted(m, this.write(m.table, { ...row, deleted: true, deleteBatchId: batch }, user.id))
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
