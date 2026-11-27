import { describe, expect, it } from 'vitest'
import { until } from '@/test/memoryDb'
import { LiveChannel, type ChangesMessage, type LiveHandlers, type SocketLike } from './live'

/** A socket the test plays the server of. */
class ScriptedSocket implements SocketLike {
  readyState = 0
  readonly sent: Record<string, unknown>[] = []
  closedWith: number | null = null
  onopen: ((event: unknown) => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: ((event: { code: number }) => void) | null = null
  onerror: ((event: unknown) => void) | null = null

  open() {
    this.readyState = 1
    this.onopen?.({})
  }
  send(data: string) {
    this.sent.push(JSON.parse(data) as Record<string, unknown>)
  }
  close(code = 1000) {
    this.readyState = 3
    this.closedWith = code
  }
  server(message: Record<string, unknown>) {
    this.onmessage?.({ data: JSON.stringify(message) })
  }
  drop(code: number) {
    this.readyState = 3
    this.onclose?.({ code })
  }
}

function channel(overrides: Partial<LiveHandlers> = {}, heartbeat = 60_000) {
  const socket = new ScriptedSocket()
  const events: string[] = []
  const handlers: LiveHandlers = {
    hello: async () => ({ clientId: 'c1', since: 41, accessEpoch: 3, appVersion: '1.0.0' }),
    welcome: (head) => events.push(`welcome ${head}`),
    changes: async (m) => {
      events.push(`changes ${m.cursor}`)
      return m.cursor
    },
    reset: () => events.push('reset'),
    session: (state) => events.push(`session ${state}`),
    app: (version) => events.push(`app ${version}`),
    editing: (m) => events.push(`${m.type} ${m.itemId}`),
    closed: (code, welcomed) => events.push(`closed ${code} ${welcomed}`),
    ...overrides,
  }
  const live = new LiveChannel(() => socket, 'ws://test/api/v1/sync/live', handlers, heartbeat)
  return { live, socket, events }
}

const changes = (cursor: number): ChangesMessage & Record<string, unknown> => ({
  type: 'changes',
  changes: {},
  evictions: [],
  cursor,
  more: false,
})

describe('the live channel (docs/03 §11.4)', () => {
  it('says hello with the cursor, is live after the welcome, and routes the server messages', async () => {
    const { live, socket, events } = channel()
    live.open()
    socket.open()
    await until(() => socket.sent.length === 1, 'the hello')
    expect(socket.sent[0]).toEqual({ type: 'hello', clientId: 'c1', since: 41, accessEpoch: 3, appVersion: '1.0.0' })
    expect(live.live).toBe(false)
    socket.server({ type: 'welcome', head: 50 })
    expect(live.live).toBe(true)
    socket.server({ type: 'ping' })
    expect(socket.sent.at(-1)).toEqual({ type: 'pong' })
    socket.server({ type: 'reset' })
    socket.server({ type: 'session', state: 'rights_changed' })
    socket.server({ type: 'app', version: '1.3.0' })
    socket.server({ type: 'editing', itemId: 'i1', target: 'description', user: 'Ana', since: 'now' })
    socket.server({ type: 'something new' })
    expect(events).toEqual(['welcome 50', 'reset', 'session rights_changed', 'app 1.3.0', 'editing i1'])
  })

  it('applies batches one after the other and acknowledges each with its cursor', async () => {
    const order: string[] = []
    let release: () => void = () => undefined
    const { live, socket } = channel({
      changes: async (m) => {
        order.push(`start ${m.cursor}`)
        if (m.cursor === 60) await new Promise<void>((r) => (release = r))
        order.push(`end ${m.cursor}`)
        return m.cursor
      },
    })
    live.open()
    socket.open()
    socket.server({ type: 'welcome', head: 70 })
    socket.server(changes(60))
    socket.server(changes(70))
    await until(() => order.length === 1, 'the first batch')
    expect(socket.sent.filter((m) => m.type === 'ack')).toEqual([])
    release()
    await until(() => socket.sent.filter((m) => m.type === 'ack').length === 2, 'two acks')
    expect(order).toEqual(['start 60', 'end 60', 'start 70', 'end 70'])
    expect(socket.sent.filter((m) => m.type === 'ack')).toEqual([
      { type: 'ack', cursor: 60 },
      { type: 'ack', cursor: 70 },
    ])
  })

  it('reports a close by the server with its code; a close of its own reports nothing', async () => {
    const first = channel()
    first.live.open()
    first.socket.open()
    first.socket.server({ type: 'welcome', head: 1 })
    first.socket.drop(4409)
    expect(first.events.at(-1)).toBe('closed 4409 true')
    expect(first.live.live).toBe(false)

    const second = channel()
    second.live.open()
    second.socket.open()
    second.live.close()
    expect(second.socket.closedWith).toBe(1000)
    expect(second.events.filter((e) => e.startsWith('closed'))).toEqual([])
  })

  it('pings every heartbeat and gives up on a server that stays silent for two', async () => {
    const { live, socket, events } = channel({}, 20)
    live.open()
    socket.open()
    socket.server({ type: 'welcome', head: 1 })
    await until(() => socket.sent.some((m) => m.type === 'ping'), 'a ping')
    await until(() => events.some((e) => e.startsWith('closed')), 'the silent close', 1000)
    expect(events.at(-1)).toBe('closed 4000 true')
    expect(socket.closedWith).toBe(4000)
  })
})
