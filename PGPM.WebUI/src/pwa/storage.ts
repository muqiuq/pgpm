export type Persistence = 'granted' | 'denied' | 'unsupported'

/**
 * Asks the browser to keep this origin's storage (PLM's requestPersistence(), docs/03 §11.2): without the grant,
 * IndexedDB is best-effort – dropped under storage pressure, and by Safari after a period without use, unsynced edits
 * with it. Idempotent and silent where granted already; phase 1 calls it after each sign-in.
 */
export async function requestPersistence(
  storage: StorageManager | undefined = navigator.storage,
): Promise<Persistence> {
  if (!storage?.persist) {
    return 'unsupported'
  }
  try {
    const granted = (await storage.persisted?.()) || (await storage.persist())
    return granted ? 'granted' : 'denied'
  } catch {
    return 'unsupported'
  }
}
