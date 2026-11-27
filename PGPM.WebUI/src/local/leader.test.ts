import { afterEach, describe, expect, it } from 'vitest'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import { requestLeadership, type LockManagerLike } from './leader'

/** Exclusive locks as the browser grants them: one holder, the others queued in order; an abort leaves the queue. */
class FakeLocks implements LockManagerLike {
  private held = false
  private readonly queue: (() => void)[] = []

  request(_name: string, options: { signal?: AbortSignal }, callback: (lock: unknown) => Promise<unknown>) {
    return new Promise<unknown>((resolve, reject) => {
      const run = () => {
        this.held = true
        void callback({}).then((value) => {
          this.held = false
          resolve(value)
          this.queue.shift()?.()
        })
      }
      if (!this.held) {
        run()
        return
      }
      this.queue.push(run)
      options.signal?.addEventListener('abort', () => {
        const at = this.queue.indexOf(run)
        if (at >= 0) this.queue.splice(at, 1)
        reject(new DOMException('aborted', 'AbortError'))
      })
    })
  }
}

describe('the leader tab (docs/03 §11.3)', () => {
  const clients: TestClient[] = []
  afterEach(async () => {
    for (const c of clients.splice(0)) {
      await c.engine.stop()
      await c.db.delete()
    }
  })

  it('leads at once without Web Locks', () => {
    let led = 0
    requestLeadership(() => led++, { locks: null })
    expect(led).toBe(1)
  })

  it('lets one tab lead; the next one takes over when it stops', async () => {
    const locks = new FakeLocks()
    const order: string[] = []
    const first = requestLeadership(() => order.push('first'), { locks })
    const second = requestLeadership(() => order.push('second'), { locks })
    await until(() => order.length === 1, 'a leader')
    expect(order).toEqual(['first'])
    first.release()
    await until(() => order.length === 2, 'the next leader')
    expect(order).toEqual(['first', 'second'])
    second.release()
  })

  it('runs one sync engine per browser: the follower syncs nothing until the leader stops', async () => {
    const server = new FakeSyncServer()
    server.addUser({ id: 'u1', name: 'Ana' })
    const locks = new FakeLocks()
    const tabA = testClient(server, 'u1', { locks })
    const tabB = testClient(server, 'u1', { locks, db: tabA.db })
    clients.push(tabA, { ...tabB, db: { delete: async () => undefined } } as unknown as TestClient)
    tabA.engine.start()
    tabB.engine.start()
    await until(() => tabA.engine.status.connection === 'live', 'tab A live')
    await until(() => tabB.engine.status.connection === 'follower', 'tab B waiting')
    expect(tabA.engine.status.leader).toBe(true)
    expect(tabB.engine.status.leader).toBe(false)
    expect(server.connections.size).toBe(1)

    await tabA.engine.stop()
    await until(() => tabB.engine.status.connection === 'live', 'tab B taking over')
    expect(tabB.engine.status.leader).toBe(true)
    expect(server.connections.size).toBe(1)
  })
})
