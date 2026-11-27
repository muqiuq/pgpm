import type { Draft, DraftStore } from '@/components/markdown/services'
import { localDb, type PgpmDb } from './db'

/**
 * The editor's drafts in IndexedDB `drafts` (docs/03 §11.11, §18.5; PLM `drafts.js`): written 800 ms after typing (the
 * editor's debounce), and – because nothing can be awaited while a tab is torn down – mirrored synchronously into
 * localStorage on `pagehide`, `visibilitychange → hidden` and `beforeunload` (≤ 512 KB), promoted into IndexedDB on the
 * next start. Drafts older than 30 days, or of an object that is gone, are swept.
 */

export const mirrorKey = 'pgpm.draft.mirror'
export const mirrorMaxBytes = 512 * 1024
export const draftMaxAgeDays = 30

interface Mirrored {
  key: string
  draft: Draft
}

export interface AppDraftStore extends DraftStore {
  /** Mirrors the last written draft into localStorage, synchronously. */
  flush(): void
  /** Moves a mirrored draft into IndexedDB if it is newer; answers its key. */
  promote(): Promise<string | null>
  /** Removes stale drafts; answers their keys. */
  sweep(exists: (table: string, id: string) => Promise<boolean>, now?: number): Promise<string[]>
  /** Listens for the tab going away (once). */
  install(): () => void
}

function readMirror(storage: Storage | undefined): Mirrored | null {
  try {
    const raw = storage?.getItem(mirrorKey)
    const parsed = raw ? (JSON.parse(raw) as Mirrored) : null
    return parsed?.key && typeof parsed.draft?.text === 'string' ? parsed : null
  } catch {
    return null
  }
}

function clearMirror(storage: Storage | undefined) {
  try {
    storage?.removeItem(mirrorKey)
  } catch {
    // Nothing to clear.
  }
}

const newer = (a: Draft | null | undefined, b: Draft | null | undefined) =>
  !b || String(a?.savedAt ?? '') >= String(b.savedAt ?? '')

export function createDraftStore(
  db: () => PgpmDb = localDb,
  storage: Storage | undefined = typeof localStorage === 'undefined' ? undefined : localStorage,
): AppDraftStore {
  let last: Mirrored | null = null

  const store: AppDraftStore = {
    async read(key) {
      let stored: Draft | null = null
      try {
        const row = await db().drafts.get(key)
        stored = row ? { revision: row.revision, base: row.base, text: row.text, savedAt: row.savedAt } : null
      } catch {
        stored = null
      }
      const mirrored = readMirror(storage)
      if (mirrored?.key === key && newer(mirrored.draft, stored)) return mirrored.draft
      return stored
    },
    async write(key, draft) {
      last = { key, draft }
      await db().drafts.put({ key, ...draft })
    },
    async remove(key) {
      if (last?.key === key) last = null
      if (readMirror(storage)?.key === key) clearMirror(storage)
      await db().drafts.delete(key)
    },
    flush() {
      if (!last || !storage) return
      try {
        const payload = JSON.stringify(last)
        if (payload.length > mirrorMaxBytes) {
          clearMirror(storage)
          return
        }
        storage.setItem(mirrorKey, payload)
      } catch {
        // Private mode, quota: the IndexedDB copy stands.
      }
    },
    async promote() {
      const mirrored = readMirror(storage)
      if (!mirrored) return null
      try {
        const existing = await db().drafts.get(mirrored.key)
        const promoted = newer(mirrored.draft, existing ?? null)
        if (promoted) await db().drafts.put({ key: mirrored.key, ...mirrored.draft })
        clearMirror(storage)
        return promoted ? mirrored.key : null
      } catch {
        return null
      }
    },
    async sweep(exists, now = Date.now()) {
      const swept: string[] = []
      try {
        for (const row of await db().drafts.toArray()) {
          const saved = Date.parse(row.savedAt)
          const old = Number.isFinite(saved) && now - saved > draftMaxAgeDays * 86_400_000
          const [table, id] = row.key.split(':')
          const gone = !row.key.startsWith('new:') && !!table && !!id && !(await exists(table, id))
          if (old || gone) {
            await db().drafts.delete(row.key)
            swept.push(row.key)
          }
        }
      } catch {
        // No database: nothing to sweep.
      }
      return swept
    },
    install() {
      if (typeof window === 'undefined') return () => undefined
      const onHidden = () => {
        if (document.visibilityState === 'hidden') store.flush()
      }
      const flush = () => store.flush()
      window.addEventListener('pagehide', flush)
      window.addEventListener('beforeunload', flush)
      document.addEventListener('visibilitychange', onHidden)
      return () => {
        window.removeEventListener('pagehide', flush)
        window.removeEventListener('beforeunload', flush)
        document.removeEventListener('visibilitychange', onHidden)
      }
    },
  }
  return store
}

/** The app's draft store (provided to the Markdown editor in App.vue). */
export const appDrafts = createDraftStore()
