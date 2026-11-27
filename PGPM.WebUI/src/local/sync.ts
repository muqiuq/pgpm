import { reactive } from 'vue'
import { ApiError } from '@/api/client'
import type { SyncRequest, SyncResponse, WireMutation } from '@/api/sync'
import { applyChanges, applyResults, clearSyncedTables, syncScope, type EvictedRoot, type ResultSummary } from './apply'
import type { Resolved } from './conflicts'
import { localDb, type PgpmDb } from './db'
import { requestLeadership, type Leadership, type LockManagerLike } from './leader'
import { closeCodes, LiveChannel, type ChangesMessage, type EditingMessage, type SocketFactory } from './live'
import type { OutboxEntry } from './outbox'
import { uuidv7 } from './uuid'

/**
 * The sync engine (docs/03 §11.7; PLM `sync.js`, adapted to a shared, live data set):
 *
 * - **Leader tab** (leader.ts): only the tab holding the Web Lock runs the HTTP loop and the WebSocket; the others hear
 *   its state on a BroadcastChannel and forward their local changes and *Sync now* to it.
 * - **Start**: HTTP sync – push the outbox in requests of ≤ 200, pull pages until `!more` – then the live channel
 *   (`hello` with the cursor) → **live**.
 * - **Live**: a completed local operation → 150 ms → HTTP push with `pull: false`; incoming `changes` → applied →
 *   `ack`.
 * - **Live channel down**: reconnect after 1, 2, 5, 10, 30 s; meanwhile HTTP sync every 30 s (5 min while hidden) and
 *   1.5 s after a local change. Also on `online`, on `visibilitychange → visible`, on the service worker's
 *   `pgpm-sync`. HTTP errors back off 5 s → 15 s → 60 s → 5 min.
 * - **Reset** (response `reset`, a `reset` message, close 4409): push what is left, empty the synced stores (the
 *   conflicts stay), pull from 0.
 * - `sync()` answers a {@link SyncResult} and never throws.
 */

export type Connection = 'stopped' | 'starting' | 'live' | 'online' | 'offline' | 'signed-out' | 'follower'

export interface SyncStatus {
  /** This tab runs the sync. */
  leader: boolean
  connection: Connection
  /** An HTTP catch-up (a pull) is running. */
  syncing: boolean
  cursor: number
  lastSyncAt: string | null
  /** The last failure, until a sync succeeds. */
  error: string | null
  /** The live channel refused for good (origin): polling only. */
  liveRefused: boolean
}

export interface SyncResult {
  ok: boolean
  /** The server wasn't reached (or didn't answer in time). */
  offline?: boolean
  signedOut?: boolean
  status?: number
  error?: string
  pushed: number
  pulled: number
  conflicts: number
  rejected: number
  evicted: number
  reset: boolean
  /** A follower tab asked the leader instead. */
  delegated?: boolean
}

export interface Notice {
  severity: 'success' | 'info' | 'warn' | 'error'
  summary: string
  detail?: string
}

export interface Timing {
  livePush: number
  httpPush: number
  poll: number
  pollHidden: number
  reconnect: readonly number[]
  errorBackoff: readonly number[]
  heartbeat: number
}

export const defaultTiming: Timing = {
  livePush: 150,
  httpPush: 1500,
  poll: 30_000,
  pollHidden: 5 * 60_000,
  reconnect: [1000, 2000, 5000, 10_000, 30_000],
  errorBackoff: [5000, 15_000, 60_000, 5 * 60_000],
  heartbeat: 25_000,
}

/** What crosses between the tabs (BroadcastChannel `pgpm-sync`). */
export type TabMessage =
  | { type: 'status'; status: SyncStatus }
  | { type: 'changed' }
  | { type: 'sync-now' }
  | { type: 'status-request' }
  | { type: 'editing'; message: EditingMessage }

export interface ChannelLike {
  postMessage(message: TabMessage): void
  onmessage: ((event: { data: TabMessage }) => void) | null
  close(): void
}

export interface EngineOptions {
  db?: () => PgpmDb
  post: (body: SyncRequest, signal?: AbortSignal) => Promise<SyncResponse>
  /** The live channel's sockets; null: polling only. */
  socket?: SocketFactory | null
  liveUrl?: () => string
  /** The app's version, for the server's diagnostics (`hello`, the sync request). */
  appVersion: string | (() => string)
  /** Web Locks; null: this tab leads (no coordination). Default: navigator.locks. */
  locks?: LockManagerLike | null
  /** The tabs' channel; null: none. Default: a BroadcastChannel. */
  channel?: ChannelLike | null
  online?: () => boolean
  hidden?: () => boolean
  /** Asks the service worker for a Background Sync (pwa/register.ts). */
  backgroundSync?: () => Promise<boolean>
  notify?: (notice: Notice) => void
  onSignedOut?: () => void
  onSession?: (state: string) => void
  /** A reset re-pulled everything (access changed): what the session knows (groups, rights) may be stale too. */
  onReset?: () => void
  onAppVersion?: (version: string) => void
  onEditing?: (message: EditingMessage) => void
  /**
   * After every successful run of the leader (its push reached the server): the file pipeline uploads the bytes of
   * the `files` rows the server has now and prefetches images (docs/03 §10.2). Not awaited; must not throw.
   */
  afterRun?: (result: SyncResult) => unknown
  /** Listen to window/document events (online, visibilitychange, the worker's pgpm-sync). Default true. */
  listen?: boolean
  timing?: Partial<Timing>
  /** Rows per table per page. */
  pageLimit?: number
  /** Mutations per request (≤ 200). */
  pushLimit?: number
}

interface SyncOptions {
  /** Pull pages too (default: unless live). */
  pull?: boolean
  /** Reset: push, empty the synced stores, pull from 0. */
  reset?: boolean
}

/** An entry as pushed: without the local `seq` and `sentAt`. */
function wire(entry: OutboxEntry): WireMutation {
  const copy: Partial<OutboxEntry> = { ...entry }
  delete copy.seq
  delete copy.sentAt
  return copy as WireMutation
}

function emptyResult(): SyncResult {
  return { ok: true, pushed: 0, pulled: 0, conflicts: 0, rejected: 0, evicted: 0, reset: false }
}

function defaultChannel(): ChannelLike | null {
  try {
    return typeof BroadcastChannel === 'undefined'
      ? null
      : (new BroadcastChannel('pgpm-sync') as unknown as ChannelLike)
  } catch {
    return null
  }
}

export class SyncEngine {
  readonly status: SyncStatus = reactive({
    leader: false,
    connection: 'stopped',
    syncing: false,
    cursor: 0,
    lastSyncAt: null,
    error: null,
    liveRefused: false,
  })

  private readonly options: EngineOptions
  private readonly timing: Timing
  private leadership: Leadership | null = null
  private live: LiveChannel | null = null
  private channel: ChannelLike | null = null
  private current: Promise<SyncResult> | null = null
  private next: { options: SyncOptions; promise: Promise<SyncResult> } | null = null
  private timers: Partial<Record<'push' | 'poll' | 'reconnect' | 'retry', ReturnType<typeof setTimeout>>> = {}
  private reconnectAttempt = 0
  private errorAttempt = 0
  private reopenLive = false
  /** The live channel said `reset`: its close 4409 that follows needs no second reset. */
  private resetAnnounced = false
  private running = false
  private abort: AbortController | null = null
  private detach: (() => void)[] = []

  constructor(options: EngineOptions) {
    this.options = options
    this.timing = { ...defaultTiming, ...options.timing }
  }

  private db(): PgpmDb {
    return (this.options.db ?? localDb)()
  }

  private get appVersion(): string {
    const version = this.options.appVersion
    return typeof version === 'function' ? version() : version
  }

  private notify(notice: Notice) {
    this.options.notify?.(notice)
  }

  private get isOnline(): boolean {
    return this.options.online?.() ?? (typeof navigator === 'undefined' ? true : navigator.onLine !== false)
  }

  private get isHidden(): boolean {
    return this.options.hidden?.() ?? (typeof document !== 'undefined' && document.visibilityState === 'hidden')
  }

  /** The live channel is welcomed: changes arrive over it. */
  get isLive(): boolean {
    return this.live?.live ?? false
  }

  // --- start, stop, leadership ---------------------------------------------------------------------------------

  /** Starts syncing in this tab: as the leader once it holds the lock, as a follower meanwhile. */
  start() {
    if (this.running) return
    this.running = true
    this.status.connection = 'starting'
    this.status.error = null
    this.channel = this.options.channel !== undefined ? this.options.channel : defaultChannel()
    if (this.channel) this.channel.onmessage = (event) => this.onTabMessage(event.data)
    if (this.options.listen !== false) this.listen()
    this.leadership = requestLeadership(() => void this.lead(), {
      locks: this.options.locks,
      onWaiting: () => {
        if (this.status.leader) return
        this.status.connection = 'follower'
        this.channel?.postMessage({ type: 'status-request' })
      },
    })
  }

  /** Stops: the lock is given back (another tab takes over), the socket closed, timers cleared. */
  async stop() {
    if (!this.running) return
    this.running = false
    this.leadership?.release()
    this.leadership = null
    this.live?.close()
    this.live = null
    for (const key of Object.keys(this.timers) as (keyof typeof this.timers)[]) clearTimeout(this.timers[key])
    this.timers = {}
    for (const off of this.detach) off()
    this.detach = []
    this.channel?.close()
    this.channel = null
    this.abort?.abort()
    await this.current
    await this.next?.promise
    // Started again meanwhile (a quick sign-in): that run's status stays.
    if (!this.running) Object.assign(this.status, { leader: false, connection: 'stopped', syncing: false })
  }

  private async lead() {
    if (!this.running) return
    this.status.leader = true
    this.status.connection = 'starting'
    try {
      const db = this.db()
      this.status.cursor = await db.getMeta('cursor', 0)
      this.status.lastSyncAt = await db.getMeta<string | null>('lastSyncAt', null)
    } catch {
      // The database opens with the first sync, or tells there why not.
    }
    if (this.options.socket) {
      this.live = new LiveChannel(
        this.options.socket,
        (this.options.liveUrl ?? (() => '/api/v1/sync/live'))(),
        {
          hello: async () => ({
            clientId: await this.clientId(),
            since: await this.db().getMeta('cursor', 0),
            accessEpoch: await this.db().getMeta('accessEpoch', 0),
            appVersion: this.appVersion,
          }),
          welcome: () => this.onWelcome(),
          changes: (message) => this.onChanges(message),
          reset: () => {
            this.resetAnnounced = true
            void this.sync({ pull: true, reset: true })
          },
          session: (state) => this.options.onSession?.(state),
          app: (version) => this.options.onAppVersion?.(version),
          editing: (message) => {
            this.options.onEditing?.(message)
            this.channel?.postMessage({ type: 'editing', message })
          },
          closed: (code, welcomed) => this.onLiveClosed(code, welcomed),
        },
        this.timing.heartbeat,
      )
    }
    await this.sync({ pull: true })
  }

  private onTabMessage(message: TabMessage) {
    if (this.status.leader) {
      if (message.type === 'changed') this.localChange()
      else if (message.type === 'sync-now') void this.sync({ pull: true })
      else if (message.type === 'status-request') this.broadcast()
      return
    }
    if (message.type === 'status') {
      Object.assign(this.status, { ...message.status, leader: false })
    } else if (message.type === 'editing') {
      this.options.onEditing?.(message.message)
    }
  }

  private broadcast() {
    if (this.status.leader) this.channel?.postMessage({ type: 'status', status: { ...this.status } })
  }

  private listen() {
    if (typeof window === 'undefined') return
    const on = (target: EventTarget, name: string, handler: () => void) => {
      target.addEventListener(name, handler)
      this.detach.push(() => target.removeEventListener(name, handler))
    }
    on(window, 'online', () => this.wake())
    on(window, 'offline', () => {
      if (!this.status.leader) return
      // The browser says it has no network: a socket it still holds (a closed lid, flight mode – DevTools' offline mode
      // even leaves it working) goes, changes queue, the indicator says Offline; `online` catches up and reopens it.
      this.live?.close()
      this.setConnection('offline')
    })
    on(window, 'pgpm:sync', () => this.wake())
    if (typeof document !== 'undefined') {
      on(document, 'visibilitychange', () => {
        if (!this.isHidden) this.wake()
        else if (!this.isLive) this.schedulePoll()
      })
    }
  }

  /** Back online, visible again, or the worker's Background Sync: catch up now and try the live channel again. */
  wake() {
    if (!this.running) return
    if (!this.status.leader) return
    this.reconnectAttempt = 0
    if (!this.isLive) void this.sync({ pull: true })
    else void this.sync({ pull: false })
  }

  // --- triggers ------------------------------------------------------------------------------------------------

  /** A local operation completed (repo.ts): push soon – 150 ms while live, else an HTTP sync after 1.5 s. */
  localChange() {
    if (!this.running) return
    if (!this.status.leader) {
      this.channel?.postMessage({ type: 'changed' })
      return
    }
    if (this.timers.push) return
    const live = this.isLive
    this.timers.push = setTimeout(
      () => {
        this.timers.push = undefined
        void this.sync({ pull: !this.isLive })
      },
      live ? this.timing.livePush : this.timing.httpPush,
    )
  }

  /** *Sync now*: push and pull over HTTP, even while live. A follower asks the leader. */
  syncNow(): Promise<SyncResult> {
    if (this.running && !this.status.leader) {
      this.channel?.postMessage({ type: 'sync-now' })
      return Promise.resolve({ ...emptyResult(), delegated: true })
    }
    return this.sync({ pull: true })
  }

  /** One sync run at a time; a request meanwhile runs once after it (requests merged). Never throws. */
  sync(options: SyncOptions = {}): Promise<SyncResult> {
    if (this.next) {
      this.next.options = {
        pull: this.next.options.pull || options.pull,
        reset: this.next.options.reset || options.reset,
      }
      return this.next.promise
    }
    if (!this.current) return this.begin(options)
    const next = { options: { ...options }, promise: Promise.resolve(emptyResult()) }
    next.promise = this.current.then(() => {
      this.next = null
      return this.begin(next.options)
    })
    this.next = next
    return next.promise
  }

  private begin(options: SyncOptions): Promise<SyncResult> {
    const run = this.run(options).then((result) => {
      this.afterSync(result)
      return result
    })
    this.current = run
    void run.finally(() => {
      if (this.current === run) this.current = null
    })
    return run
  }

  // --- the run -------------------------------------------------------------------------------------------------

  private async clientId(): Promise<string> {
    const db = this.db()
    let id = await db.getMeta<string | null>('clientId', null)
    if (!id) {
      id = uuidv7()
      await db.setMeta('clientId', id)
    }
    return id
  }

  /** The next ≤ 200 entries in order, marked as sent (never coalesced again). */
  private async takeBatch(db: PgpmDb): Promise<OutboxEntry[]> {
    const limit = Math.min(this.options.pushLimit ?? 200, 200)
    return db.transaction('rw', db.outbox, async () => {
      const entries = await db.outbox.orderBy('seq').limit(limit).toArray()
      const now = new Date().toISOString()
      for (const entry of entries) {
        if (!entry.sentAt) {
          entry.sentAt = now
          await db.outbox.put(entry)
        }
      }
      return entries
    })
  }

  private async run(options: SyncOptions): Promise<SyncResult> {
    const result = emptyResult()
    const pull = options.pull ?? !this.isLive
    let reset = options.reset ?? false
    if (pull) this.status.syncing = true
    this.broadcast()
    const abort = new AbortController()
    this.abort = abort
    try {
      const db = this.db()
      const clientId = await this.clientId()
      let cursor = await db.getMeta('cursor', 0)
      let epoch = await db.getMeta('accessEpoch', 0)
      let pulling = pull
      let requests = 0
      for (let round = 0; round < 100; round++) {
        const sent = await this.takeBatch(db)
        const resolved = await db.getMeta<Resolved[]>('resolved', [])
        if (!sent.length && !pulling && !resolved.length && !reset) break
        const body: SyncRequest = {
          clientId,
          appVersion: this.appVersion,
          since: cursor,
          accessEpoch: epoch,
          limit: this.options.pageLimit ?? 500,
          mutations: sent.map(wire),
          resolved,
          pull: pulling && !reset,
          pending: await db.outbox.count(),
          conflicts: await db.conflicts.count(),
        }
        const response = await this.options.post(body, abort.signal)
        requests++
        result.pushed += sent.length
        const summary: ResultSummary = await db.transaction('rw', syncScope(db), async () => {
          const settled = await applyResults(db, sent, response.results ?? [], response.conflicts ?? [])
          // Reported: they go; what was resolved meanwhile was appended and stays for the next request.
          if (resolved.length) {
            await db.setMeta('resolved', (await db.getMeta<Resolved[]>('resolved', [])).slice(resolved.length))
          }
          return settled
        })
        result.conflicts += summary.conflicts
        result.rejected += summary.rejected.length
        this.reportRejected(summary.rejected)
        const answered = sent.filter((e) => (response.results ?? []).some((r) => r.mutationId === e.mutationId))

        if (response.reset || reset) {
          const left = await db.outbox.count()
          // Push what is left first, while the server answers; what it doesn't stays in the outbox and goes out
          // after the reset (the outbox is never wiped).
          if (left > 0 && answered.length > 0) continue
          await db.transaction('rw', syncScope(db), async () => {
            await clearSyncedTables(db)
            await db.setMeta('cursor', 0)
            await db.setMeta('accessEpoch', response.accessEpoch ?? epoch)
          })
          cursor = 0
          epoch = response.accessEpoch ?? epoch
          reset = false
          pulling = true
          result.reset = true
          this.status.cursor = 0
          this.reopenLive = true
          this.live?.close()
          continue
        }

        if (typeof response.accessEpoch === 'number' && response.accessEpoch !== epoch) {
          epoch = response.accessEpoch
          await db.setMeta('accessEpoch', epoch)
        }
        if (pulling) {
          const page = await db.transaction('rw', syncScope(db), async () => {
            const applied = await applyChanges(db, response.changes, response.evictions)
            const at = await db.getMeta('cursor', 0)
            await db.setMeta('cursor', Math.max(at, response.cursor ?? at))
            return applied
          })
          result.pulled += page.rows
          result.evicted += page.evicted.length
          this.reportEvicted(page.evicted)
          const advanced = (response.cursor ?? cursor) > cursor
          cursor = Math.max(cursor, response.cursor ?? cursor)
          this.status.cursor = cursor
          if (response.more && advanced) continue
          if (response.more) console.warn(`PGPM sync: the cursor could not advance past ${cursor}`)
          pulling = false
        }
        // More to push: the batch was full, or entries came meanwhile; a batch without any answer stops here.
        if (sent.length && !answered.length) break
        if ((await db.outbox.filter((e) => !e.sentAt).count()) > 0 || sent.length >= (this.options.pushLimit ?? 200)) {
          continue
        }
        break
      }
      if (requests) {
        const at = new Date().toISOString()
        await db.setMeta('lastSyncAt', at)
        this.status.lastSyncAt = at
      }
      this.status.error = null
      if (result.reset) this.options.onReset?.()
      if (result.conflicts > 0) {
        this.notify({
          severity: 'warn',
          summary: result.conflicts === 1 ? '1 sync conflict' : `${result.conflicts} sync conflicts`,
          detail: 'Someone else changed the same thing. Open the sync status to decide.',
        })
      }
      return result
    } catch (error) {
      return { ...result, ...this.failure(error) }
    } finally {
      if (this.abort === abort) this.abort = null
      this.status.syncing = false
      this.broadcast()
    }
  }

  private failure(error: unknown): Partial<SyncResult> {
    if (error instanceof ApiError) {
      if (error.status === 0) return { ok: false, offline: true, status: 0, error: error.message }
      if (error.status === 401) return { ok: false, signedOut: true, status: 401, error: error.message }
      return { ok: false, status: error.status, error: error.message }
    }
    if (error instanceof DOMException && (error.name === 'AbortError' || error.name === 'TimeoutError')) {
      return { ok: false, offline: true, status: 0, error: 'The server did not answer in time.' }
    }
    console.error('PGPM sync failed', error)
    return { ok: false, error: error instanceof Error ? error.message : String(error) }
  }

  private reportRejected(rejected: ResultSummary['rejected']) {
    for (const r of rejected) {
      this.notify({
        severity: 'error',
        summary: 'Change undone',
        detail: `${r.title ? `${r.title}: ` : ''}${r.message}`,
      })
    }
  }

  private reportEvicted(evicted: readonly EvictedRoot[]) {
    for (const root of evicted) {
      this.notify({
        severity: 'info',
        summary: 'Access removed',
        detail: root.title
          ? `Access to “${root.title}” was removed; it is no longer on this device.`
          : 'Access to an item was removed.',
      })
    }
  }

  private setConnection(connection: Connection) {
    if (this.status.connection !== connection) {
      this.status.connection = connection
      this.broadcast()
    }
  }

  /** What follows a run: the live channel, polling, a retry with backoff, or waiting for a sign-in. */
  private afterSync(result: SyncResult) {
    if (!this.running || !this.status.leader || result.delegated) return
    clearTimeout(this.timers.retry)
    this.timers.retry = undefined
    if (result.ok) {
      this.errorAttempt = 0
      try {
        void Promise.resolve(this.options.afterRun?.(result)).catch(() => undefined)
      } catch {
        // The uploads retry with the next run.
      }
      if (!this.isLive) this.setConnection('online')
      if (this.live && !this.status.liveRefused && (this.reopenLive || (!this.live.live && !this.live.connecting))) {
        this.reopenLive = false
        if (!this.timers.reconnect) this.openLive()
      }
      if (!this.isLive) this.schedulePoll()
      return
    }
    this.status.error = result.error ?? 'Sync failed'
    if (result.signedOut) {
      this.setConnection('signed-out')
      this.live?.close()
      for (const key of ['push', 'poll', 'reconnect'] as const) clearTimeout(this.timers[key])
      this.timers = {}
      this.options.onSignedOut?.()
      return
    }
    if (result.offline) {
      if (!this.isLive) this.setConnection('offline')
      void this.db()
        .outbox.count()
        .then((n) => (n > 0 ? this.options.backgroundSync?.() : undefined))
        .catch(() => undefined)
    }
    const backoff = this.timing.errorBackoff
    const delay = backoff[Math.min(this.errorAttempt, backoff.length - 1)]!
    this.errorAttempt++
    this.timers.retry = setTimeout(() => {
      this.timers.retry = undefined
      void this.sync({ pull: !this.isLive })
    }, delay)
    this.broadcast()
  }

  private schedulePoll() {
    clearTimeout(this.timers.poll)
    if (!this.running || this.isLive) return
    this.timers.poll = setTimeout(
      () => {
        this.timers.poll = undefined
        if (!this.isLive) void this.sync({ pull: true })
      },
      this.isHidden ? this.timing.pollHidden : this.timing.poll,
    )
  }

  // --- the live channel ----------------------------------------------------------------------------------------

  private openLive() {
    if (!this.live || !this.running || this.status.liveRefused) return
    if (!this.isOnline) return
    this.resetAnnounced = false
    this.live.open()
  }

  private onWelcome() {
    this.reconnectAttempt = 0
    clearTimeout(this.timers.poll)
    this.timers.poll = undefined
    this.setConnection('live')
    void this.db()
      .outbox.count()
      .then((n) => {
        if (n > 0) void this.sync({ pull: false })
      })
      .catch(() => undefined)
  }

  private async onChanges(message: ChangesMessage): Promise<number> {
    const db = this.db()
    const applied = await db.transaction('rw', syncScope(db), async () => {
      const page = await applyChanges(db, message.changes, message.evictions)
      const at = await db.getMeta('cursor', 0)
      await db.setMeta('cursor', Math.max(at, message.cursor))
      return page
    })
    this.status.cursor = Math.max(this.status.cursor, message.cursor)
    this.reportEvicted(applied.evicted)
    return message.cursor
  }

  private onLiveClosed(code: number, welcomed: boolean) {
    if (!this.running) return
    if (welcomed || this.status.connection === 'live') {
      this.setConnection(this.isOnline ? 'online' : 'offline')
    }
    if (code === closeCodes.signedOut) {
      this.setConnection('signed-out')
      this.options.onSignedOut?.()
      return
    }
    if (code === closeCodes.origin) {
      this.status.liveRefused = true
      this.status.error = 'The live channel was refused; changes are fetched every 30 s.'
      this.schedulePoll()
      return
    }
    if (code === closeCodes.reset) {
      if (!this.resetAnnounced) void this.sync({ pull: true, reset: true })
      this.resetAnnounced = false
      return
    }
    const steps = this.timing.reconnect
    const delay =
      code === closeCodes.tooMany ? steps[steps.length - 1]! : steps[Math.min(this.reconnectAttempt, steps.length - 1)]!
    this.reconnectAttempt++
    clearTimeout(this.timers.reconnect)
    this.timers.reconnect = setTimeout(() => {
      this.timers.reconnect = undefined
      this.openLive()
    }, delay)
    // Meanwhile: catch up over HTTP now and keep polling.
    if (welcomed) void this.sync({ pull: true })
    else this.schedulePoll()
  }
}
