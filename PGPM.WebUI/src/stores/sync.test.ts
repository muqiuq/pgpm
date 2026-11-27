import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it } from 'vitest'
import { engine } from '@/local/boot'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import { useSyncStore } from './sync'

describe('the first sync after signing in (docs/03 §12.6)', () => {
  const db = memoryDb()
  afterEach(async () => {
    engine.status.connection = 'stopped'
    useDatabase(null)
    await db.delete()
  })

  it('runs while the engine does and this device never synced; one sync ends it', async () => {
    useDatabase(db)
    setActivePinia(createPinia())
    const sync = useSyncStore()
    // A stopped engine (signed out) never syncs: nothing to wait for.
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(sync.firstSync).toBe(false)
    engine.status.connection = 'starting'
    await until(() => sync.firstSync, 'the first sync')
    engine.status.connection = 'live'
    expect(sync.firstSync).toBe(true)
    await db.setMeta('lastSyncAt', '2026-11-27T10:00:00Z')
    await until(() => !sync.firstSync, 'synced once')
  })
})
