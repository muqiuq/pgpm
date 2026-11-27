import { ApiError } from '@/api/client'
import type {
  Eviction,
  FieldConflict,
  MutationResult,
  ShareSpec,
  SyncRequest,
  SyncResponse,
  TrashEntry,
  WireMutation,
} from '@/api/sync'
import type { SocketLike } from '@/local/live'
import { applyPatch, getPath, jsonEqual, type Row } from '@/local/outbox'
import { accessRank, type Access } from '@/local/rows'
import { uuidv7 } from '@/local/uuid'

/**
 * An in-memory server for the sync tests: the contract of docs/03 §11.4–§11.6 as the client relies on it – push with
 * per-field conflicts, delete conflicts and `force`, idempotent mutation ids, sharing visibility with computed `access`,
 * re-stamps and evictions, access epochs and resets, trash and restore, and the live channel with ack flow control.
 * Also a readable statement of what the client expects from PGPM's server.
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
  items: ['title', 'description', 'color', 'imageFileId', 'statusId', 'pinned'],
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

  private now() {
    return new Date().toISOString()
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
    for (const share of this.table('item_shares').values()) {
      if (share.itemId !== item.id || !user.groupIds.includes(share.groupId as string)) continue
      const live = !share.deleted || (item.deleted && share.deleteBatchId === item.deleteBatchId)
      if (!live) continue
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
    const more = truncated.length > 0
    const cursor = more ? Math.min(...truncated) : this.seq
    const evictions: Eviction[] = this.evictions
      .filter((e) => e.userId === userId && e.seq > since && e.seq <= cursor)
      .map((e) => ({ kind: e.kind, id: e.id }))
    return { changes, evictions, cursor, more }
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
    const reset = body.accessEpoch !== user.accessEpoch || body.since < this.horizon
    const response: SyncResponse = {
      head: this.seq,
      cursor: body.since,
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

  private rejected(mutation: WireMutation, code: string, current: Row | null = null): Stored {
    return {
      result: { mutationId: mutation.mutationId, status: 'rejected', id: mutation.id, code, message: code, current },
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
    if (!rows || m.table === 'groups') return this.rejected(m, 'read_only')
    const existing = rows.get(m.id)
    if (m.op === 'insert') return this.insert(user, m, existing)
    if (!existing) return this.rejected(m, 'not_found')
    if (!this.visible(user, m.table, existing)) return this.rejected(m, 'missing_access')
    if (m.op === 'patch') return this.patch(user, m, existing)
    return this.remove(user, m, existing)
  }

  private insert(user: User, m: WireMutation, existing: ServerRow | undefined): Stored {
    if (existing) return this.rejected(m, 'duplicate_id', this.wireRow(user, m.table, existing))
    const row: Row & { id: string } = { ...(m.row ?? {}), id: m.id }
    if (m.table === 'items') {
      if (row.type !== 'note') return this.rejected(m, 'validation_failed')
      if (typeof row.title !== 'string' || !row.title.trim()) return this.rejected(m, 'validation_failed')
      const number = (this.numbers.get('note') ?? 0) + 1
      this.numbers.set('note', number)
      const stored = this.write('items', { ...row, ownerUserId: user.id, number }, user.id)
      if (!this.created.has(m.batch)) this.created.set(m.batch, new Set())
      this.created.get(m.batch)!.add(m.id)
      return this.accepted(m, stored)
    }
    if (m.table === 'item_shares') {
      const item = this.table('items').get(row.itemId as string)
      if (!item || item.ownerUserId !== user.id || !this.created.get(m.batch)?.has(item.id)) {
        return this.rejected(m, 'missing_access')
      }
      const duplicate = [...this.table('item_shares').values()].find(
        (s) => !s.deleted && s.itemId === row.itemId && s.groupId === row.groupId,
      )
      if (duplicate) return this.accepted(m, duplicate, { mergedInto: duplicate.id })
      return this.accepted(m, this.write('item_shares', row, user.id))
    }
    if (m.table === 'user_settings') {
      const duplicate = [...this.table('user_settings').values()].find(
        (s) => !s.deleted && s.userId === user.id && s.key === row.key,
      )
      if (duplicate) return this.accepted(m, duplicate, { mergedInto: duplicate.id })
      return this.accepted(m, this.write('user_settings', { ...row, userId: user.id }, user.id))
    }
    return this.accepted(m, this.write(m.table, { ...row, source: 'manual' }, user.id))
  }

  private patch(user: User, m: WireMutation, row: ServerRow): Stored {
    if (!this.canWrite(user, m.table, row, 'edit'))
      return this.rejected(m, 'missing_access', this.wireRow(user, m.table, row))
    if (row.deleted) {
      return {
        result: {
          mutationId: m.mutationId,
          status: 'conflict',
          kind: 'deleted_by_other',
          id: row.id,
          current: { id: row.id, seq: row.seq, rev: row.rev, deleted: true, deleteBatchId: row.deleteBatchId },
        },
        conflicts: [],
      }
    }
    const allowed = patchable[m.table] ?? []
    const changes = m.changes ?? {}
    if (Object.keys(changes).some((k) => !allowed.includes(k.split('.')[0]!))) return this.rejected(m, 'not_patchable')
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
    return {
      result: {
        mutationId: m.mutationId,
        status: Object.keys(apply).length ? 'partial' : 'conflict',
        id: row.id,
        rev: stored.rev,
        seq: stored.seq,
        conflictFields: conflicts.map((c) => c.field),
      },
      conflicts,
    }
  }

  private remove(user: User, m: WireMutation, row: ServerRow): Stored {
    if (m.table === 'item_shares') return this.rejected(m, 'read_only')
    if (!this.canWrite(user, m.table, row, 'manage'))
      return this.rejected(m, 'missing_access', this.wireRow(user, m.table, row))
    if (row.deleted) return this.accepted(m, row)
    if (!m.force && m.baseRev !== null && row.rev !== m.baseRev) {
      return {
        result: {
          mutationId: m.mutationId,
          status: 'conflict',
          kind: 'changed_since_delete',
          id: row.id,
          current: this.wireRow(user, m.table, row),
        },
        conflicts: [],
      }
    }
    const batch = uuidv7()
    if (m.table === 'items') {
      this.batches.set(batch, { rootKind: 'item', rootId: row.id, at: this.now(), by: user.id, restored: false })
      for (const share of this.table('item_shares').values()) {
        if (share.itemId === row.id && !share.deleted) {
          this.write('item_shares', { ...share, deleted: true, deleteBatchId: batch }, user.id)
        }
      }
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

  /** `PUT /api/v1/items/{id}/shares`: diff, re-stamp the subtree, evictions for those who lost access. */
  putShares(userId: string, itemId: string, shares: ShareSpec[]) {
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
        title: String(this.table('items').get(b.rootId)?.title ?? ''),
        deletedAt: b.at,
        deletedBy: b.by,
        deletedByName: this.users.get(b.by)?.name ?? null,
      }))
  }

  /** `POST /api/v1/trash/{batchId}/restore`. */
  restore(userId: string, batchId: string) {
    const batch = this.batches.get(batchId)
    if (!batch || batch.restored) throw new ApiError(404, { code: 'not_found' })
    for (const table of tableNames) {
      for (const row of this.table(table).values()) {
        if (row.deleteBatchId === batchId) this.write(table, { ...row, deleted: false, deleteBatchId: null }, userId)
      }
    }
    batch.restored = true
    this.publish()
  }

  /** Group membership changed (an admin): the user's access epoch goes up, live connections get `reset`. */
  setGroups(userId: string, groupIds: string[]) {
    const user = this.user(userId)
    user.groupIds = groupIds
    user.accessEpoch++
    for (const c of this.connections) if (c.userId === userId) c.send({ type: 'reset' })
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

  receive(message: Record<string, unknown>) {
    const user = this.server.users.get(this.userId)!
    switch (message.type) {
      case 'hello':
        if (message.accessEpoch !== user.accessEpoch || (message.since as number) < this.server.horizon) {
          this.drop(4409)
          return
        }
        this.cursor = message.since as number
        this.welcomed = true
        this.send({ type: 'welcome', head: this.server.seq })
        this.pump()
        break
      case 'ack':
        this.cursor = message.cursor as number
        this.awaiting = false
        this.pump()
        break
      case 'ping':
        this.send({ type: 'pong' })
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
      this.cursor = page.cursor
      return
    }
    this.awaiting = true
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
