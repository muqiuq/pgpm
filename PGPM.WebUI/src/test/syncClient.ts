import { ApiError } from '@/api/client'
import type { PgpmDb } from '@/local/db'
import type { LockManagerLike } from '@/local/leader'
import { Repo } from '@/local/repo'
import { conflictActions } from '@/local/resolve'
import { SyncEngine, type EngineOptions, type Notice } from '@/local/sync'
import type { FakeSyncServer } from './fakeSyncServer'
import { memoryDb } from './memoryDb'

/** Short timers for the tests: the rules are the same, the waits milliseconds. */
export const fastTiming = {
  livePush: 5,
  httpPush: 10,
  poll: 60_000,
  pollHidden: 60_000,
  reconnect: [10, 20, 40],
  errorBackoff: [20, 40],
  heartbeat: 60_000,
}

export interface TestClient {
  userId: string
  db: PgpmDb
  repo: Repo
  engine: SyncEngine
  notices: Notice[]
  signedOut: number
  actions: ReturnType<typeof conflictActions>
  /** This device's network. */
  network: boolean
  goOffline(): void
  goOnline(): void
}

/** A device of a user against the fake server: its own database, repository and engine. */
export function testClient(
  server: FakeSyncServer,
  userId: string,
  options: { live?: boolean; locks?: LockManagerLike | null; db?: PgpmDb; engine?: Partial<EngineOptions> } = {},
): TestClient {
  const db = options.db ?? memoryDb()
  const client = { userId, db, notices: [] as Notice[], signedOut: 0, network: true } as TestClient
  client.goOffline = () => {
    client.network = false
    server.close(userId, 1006)
  }
  client.goOnline = () => {
    client.network = true
    client.engine.wake()
  }
  client.engine = new SyncEngine({
    db: () => db,
    post: async (body) => {
      if (!client.network) throw new ApiError(0, undefined)
      return server.sync(userId, body)
    },
    socket: options.live === false ? null : () => server.connect(userId, client.network),
    appVersion: '1.0.0',
    locks: options.locks ?? null,
    channel: null,
    listen: false,
    online: () => client.network && server.online,
    timing: fastTiming,
    notify: (n) => client.notices.push(n),
    onSignedOut: () => client.signedOut++,
    ...options.engine,
  })
  client.repo = new Repo({ db: () => db, userId: () => userId, changed: () => client.engine.localChange() })
  client.actions = conflictActions({
    db: () => db,
    repo: client.repo,
    restoreBatch: async (batchId) => server.restore(userId, batchId),
    findBatch: async (_table, rowId) => server.trash(userId).find((t) => t.rootId === rowId)?.batchId ?? null,
    syncNow: () => client.engine.syncNow(),
    restoreWait: 2000,
  })
  return client
}
