import type { Eviction } from '@/api/sync'
import type { Row } from './outbox'

/**
 * The live channel (docs/03 §11.4, §12.5): the browser's own WebSocket on `/api/v1/sync/live`, no library, and its
 * messages – typed here by hand, as OpenAPI doesn't describe a WebSocket. It carries
 * no state-changing client messages – only `hello`, `ack`, `ping`/`pong` – so it adds no CSRF surface; writes stay on
 * HTTP. Incoming `changes` are applied one batch after the other and acknowledged with their cursor (the server sends
 * the next batch only after the ack). Heartbeat: a `ping` every 25 s; nothing heard for two of them → closed.
 * Reconnecting and the fallback are the sync engine's (sync.ts).
 */

/** The part of a WebSocket used here (the fake in the tests implements it). */
export interface SocketLike {
  readonly readyState: number
  send(data: string): void
  close(code?: number, reason?: string): void
  onopen: ((event: unknown) => void) | null
  onmessage: ((event: { data: unknown }) => void) | null
  onclose: ((event: { code: number; reason?: string }) => void) | null
  onerror: ((event: unknown) => void) | null
}

export type SocketFactory = (url: string) => SocketLike

/** Close codes of the live channel (docs/03 §11.4). */
export const closeCodes = {
  /** The session is invalid: sign in again. */
  signedOut: 4401,
  /** The Origin header didn't match the host. PGPM's server refuses that with HTTP 403 before the upgrade (the browser
   * sees 1006), so this code arrives only from another server. */
  origin: 4403,
  /** Reset required: run an HTTP sync with reset. */
  reset: 4409,
  /** More than 10 connections of the user. */
  tooMany: 4429,
  /** Ours: nothing heard for two heartbeats. */
  silent: 4000,
} as const

export interface ChangesMessage {
  type: 'changes'
  changes: Record<string, Row[]>
  evictions: Eviction[]
  cursor: number
  more: boolean
}

/** *… is editing* (docs/03 §18.6): never about the user's own editors; `since` only with `editing`. */
export interface EditingMessage {
  type: 'editing' | 'editing_end'
  itemId: string
  target: string
  userId: string
  /** The display name. */
  user: string
  since?: string
}

export type ServerMessage =
  | { type: 'welcome'; head: number }
  | ChangesMessage
  | { type: 'reset' }
  | { type: 'session'; state: 'rights_changed' | 'expired' | 'revoked' | string }
  | { type: 'app'; version: string }
  | { type: 'ping' }
  | { type: 'pong' }
  | EditingMessage

export interface Hello {
  clientId: string
  since: number
  accessEpoch: number
  appVersion: string
}

export interface LiveHandlers {
  /** The hello's fields, read when the socket opens. */
  hello(): Promise<Hello>
  welcome(head: number): void
  /** Applies a batch; answers the cursor to acknowledge. */
  changes(message: ChangesMessage): Promise<number>
  reset(): void
  session(state: string): void
  app(version: string): void
  editing(message: EditingMessage): void
  /** The socket closed (not by close()): the code, and whether it had been welcomed. */
  closed(code: number, welcomed: boolean): void
}

export class LiveChannel {
  private socket: SocketLike | null = null
  private welcomed = false
  private chain: Promise<void> = Promise.resolve()
  private heartbeat: ReturnType<typeof setInterval> | undefined
  private lastHeard = 0
  private readonly create: SocketFactory
  private readonly url: string
  private readonly handlers: LiveHandlers
  private readonly heartbeatMs: number

  constructor(create: SocketFactory, url: string, handlers: LiveHandlers, heartbeatMs = 25_000) {
    this.create = create
    this.url = url
    this.handlers = handlers
    this.heartbeatMs = heartbeatMs
  }

  /** Welcomed by the server: changes arrive here. */
  get live(): boolean {
    return this.welcomed && this.socket !== null
  }

  get connecting(): boolean {
    return this.socket !== null && !this.welcomed
  }

  open() {
    if (this.socket) return
    let socket: SocketLike
    try {
      socket = this.create(this.url)
    } catch {
      queueMicrotask(() => this.handlers.closed(1006, false))
      return
    }
    this.socket = socket
    this.welcomed = false
    this.chain = Promise.resolve()
    socket.onopen = () => {
      this.lastHeard = Date.now()
      void this.handlers.hello().then(
        (hello) => this.send({ type: 'hello', ...hello }),
        () => this.drop(socket, 1011),
      )
      this.startHeartbeat(socket)
    }
    socket.onmessage = (event) => this.receive(socket, event.data)
    socket.onerror = () => undefined
    socket.onclose = (event) => {
      if (this.socket !== socket) return
      const welcomed = this.welcomed
      this.cleanup()
      this.handlers.closed(event.code, welcomed)
    }
  }

  /** Closes it on purpose: no `closed` callback. */
  close() {
    const socket = this.socket
    this.cleanup()
    if (socket) {
      socket.onclose = null
      socket.onmessage = null
      try {
        socket.close(1000)
      } catch {
        // Closed already.
      }
    }
  }

  private cleanup() {
    clearInterval(this.heartbeat)
    this.heartbeat = undefined
    this.socket = null
    this.welcomed = false
  }

  /** Closes a misbehaving socket as if the server had (the engine reconnects). */
  private drop(socket: SocketLike, code: number) {
    if (this.socket !== socket) return
    const welcomed = this.welcomed
    this.cleanup()
    socket.onclose = null
    socket.onmessage = null
    try {
      socket.close(code === closeCodes.silent ? 4000 : 1000)
    } catch {
      // Closed already.
    }
    this.handlers.closed(code, welcomed)
  }

  private send(message: Record<string, unknown>) {
    try {
      if (this.socket && this.socket.readyState === 1) this.socket.send(JSON.stringify(message))
    } catch {
      // The close event follows.
    }
  }

  private startHeartbeat(socket: SocketLike) {
    clearInterval(this.heartbeat)
    this.heartbeat = setInterval(() => {
      if (Date.now() - this.lastHeard > 2 * this.heartbeatMs + Math.min(1000, this.heartbeatMs)) {
        this.drop(socket, closeCodes.silent)
        return
      }
      this.send({ type: 'ping' })
    }, this.heartbeatMs)
  }

  private receive(socket: SocketLike, data: unknown) {
    if (this.socket !== socket) return
    this.lastHeard = Date.now()
    let message: ServerMessage
    try {
      message = JSON.parse(String(data)) as ServerMessage
    } catch {
      return
    }
    switch (message.type) {
      case 'welcome':
        this.welcomed = true
        this.handlers.welcome(message.head)
        break
      case 'changes': {
        const batch = message
        this.chain = this.chain.then(async () => {
          if (this.socket !== socket) return
          try {
            const cursor = await this.handlers.changes(batch)
            if (this.socket === socket) this.send({ type: 'ack', cursor })
          } catch {
            this.drop(socket, 1011)
          }
        })
        break
      }
      case 'reset':
        this.handlers.reset()
        break
      case 'session':
        this.handlers.session(message.state)
        break
      case 'app':
        this.handlers.app(message.version)
        break
      case 'ping':
        this.send({ type: 'pong' })
        break
      case 'editing':
      case 'editing_end':
        this.handlers.editing(message)
        break
      default:
        break
    }
  }
}
