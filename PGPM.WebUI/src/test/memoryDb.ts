import Dexie from 'dexie'
import { IDBKeyRange, indexedDB } from 'fake-indexeddb'
import { openDatabase, type PgpmDb } from '@/local/db'
import { uuidv7 } from '@/local/uuid'

// Dexie's liveQuery looks for the IndexedDB it found when it loaded (none in jsdom): the fake one.
Object.assign(Dexie.dependencies, { indexedDB, IDBKeyRange })

/** A fresh local database in memory (fake-indexeddb), one per client of a test. */
export function memoryDb(name = `test-${uuidv7()}`): PgpmDb {
  return openDatabase(name, { indexedDB, IDBKeyRange })
}

/** Waits until the condition holds (the sync runs on timers and sockets), or fails after `timeout` ms. */
export async function until(condition: () => boolean | Promise<boolean>, what = 'the condition', timeout = 3000) {
  const end = Date.now() + timeout
  while (Date.now() < end) {
    if (await condition()) return
    await new Promise((r) => setTimeout(r, 5))
  }
  throw new Error(`Timed out waiting for ${what}`)
}
