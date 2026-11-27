import { inject, provide, type InjectionKey } from 'vue'
import type { Revision } from './types'

/**
 * What the Markdown components need from the app around them (docs/03 §18.4, §18.5), given with Vue's
 * provide/inject, so that the editor and the renderer know nothing of the sync, the local database or the file
 * pipeline. A view (or the app) provides them once for everything below it:
 *
 *     provideMarkdownServices({ fileSink, resolveFileUrl, drafts })
 *
 * - `FileSink` stores a pasted, dropped or picked file and gives its URL for the Markdown. Without one, the editor
 *   refuses files with a notice (phase 3 provides the offline pipeline: a `files` row, the bytes in IndexedDB, the
 *   upload when online).
 * - `FileUrlResolver` turns a file's URL in the text (`/api/v1/files/{id}/{name}`) into one the browser can show: a
 *   blob: URL from the local store, the URL itself, or null where it can't (offline and not on this device). Without
 *   one, the URL itself.
 * - `DraftStore` keeps the editor's drafts (PNMTD's, per `draftKey`). Without one, localStorage as in PNMTD (phase 2:
 *   IndexedDB with a synchronous localStorage mirror on pagehide).
 */

/** What a file becomes in the text (PNMTD docs/locations-knowledge-plan.md §5.5): an image or an attachment. */
export type FileKind = 'image' | 'attachment'

/** A file as a FileSink stored it. `url` goes into the Markdown: `/api/v1/files/{id}/{name}` (docs/03 §18.3). */
export interface StoredFile {
  id: string
  url: string
  name: string
  contentType: string
  size: number
  width?: number
  height?: number
}

/**
 * Where the editor's files go (docs/03 §18.4, step 4). The file comes prepared (useUpload's steps 1–3: the kind
 * chosen, a JPEG drawn again, a large image made smaller).
 *
 * `store` resolves with the stored file, or null when it declined and has told the person itself; it rejects with an
 * Error (an UploadError, or any other) for the editor to show. `onProgress` takes 0…1, for the editor's status line.
 */
export interface FileSink {
  store(file: File, kind: FileKind, onProgress?: (fraction: number) => void): Promise<StoredFile | null>
}

export interface ResolveOptions {
  /**
   * Only what can be shown without the network (a map card's compact text, docs/03 §18.2): null rather than a fetch.
   * A resolver without a local store may ignore it.
   */
  localOnly?: boolean
}

/** A file's URL in the text → a URL to show it from, or null where it can't be shown (docs/03 §18.4, step 3). */
export type FileUrlResolver = (url: string, options?: ResolveOptions) => Promise<string | null>

/** A draft of the editor (PNMTD §5.7, docs/03 §18.5): the text, and what it started from. */
export interface Draft {
  /** The revision the edit started from. */
  revision: Revision | null
  /** The text the edit started from: a restored draft saves against it (the patch's `old`). */
  base?: string
  text: string
  /** ISO 8601. */
  savedAt: string
}

/**
 * The editor's drafts, by its `draftKey` (`<table>:<id>:<field>`). `read` may answer at once or later (IndexedDB);
 * `write` and `remove` are fired and forgotten: a draft is a convenience, the editor goes on if one fails.
 */
export interface DraftStore {
  read(key: string): Draft | null | Promise<Draft | null>
  write(key: string, draft: Draft): void | Promise<void>
  remove(key: string): void | Promise<void>
}

export const fileSinkKey: InjectionKey<FileSink | null> = Symbol('pgpm.markdown.fileSink')
export const fileUrlResolverKey: InjectionKey<FileUrlResolver | null> = Symbol('pgpm.markdown.resolveFileUrl')
export const draftStoreKey: InjectionKey<DraftStore> = Symbol('pgpm.markdown.drafts')

/** The default resolver: the URL itself. */
export const resolveAsIs: FileUrlResolver = (url) => Promise.resolve(url)

const draftPrefix = 'pgpm.draft.'

/** PNMTD's drafts: localStorage, `pgpm.draft.<key>`. Full or blocked storage loses the draft, nothing else. */
export const localStorageDrafts: DraftStore = {
  read(key) {
    try {
      const draft = JSON.parse(localStorage.getItem(draftPrefix + key) ?? 'null') as Draft | null
      return draft && typeof draft.text === 'string' ? draft : null
    } catch {
      return null
    }
  },
  write(key, draft) {
    try {
      localStorage.setItem(draftPrefix + key, JSON.stringify(draft))
    } catch {
      // Full storage: the draft is a convenience, the editor goes on.
    }
  },
  remove(key) {
    try {
      localStorage.removeItem(draftPrefix + key)
    } catch {
      // As above.
    }
  },
}

/** The provided FileSink, or null: files are refused. */
export function useFileSink(): FileSink | null {
  return inject(fileSinkKey, null)
}

/** The provided resolver, or null: file URLs are shown as they are (no resolving step at all). */
export function useFileUrlResolver(): FileUrlResolver | null {
  return inject(fileUrlResolverKey, null)
}

/** The provided draft store, or localStorage. */
export function useDraftStore(): DraftStore {
  return inject(draftStoreKey, localStorageDrafts)
}

/** Provides the given services to every Markdown component below the calling component (in its setup). */
export function provideMarkdownServices(services: {
  fileSink?: FileSink | null
  resolveFileUrl?: FileUrlResolver | null
  drafts?: DraftStore
}) {
  if (services.fileSink !== undefined) {
    provide(fileSinkKey, services.fileSink)
  }
  if (services.resolveFileUrl !== undefined) {
    provide(fileUrlResolverKey, services.resolveFileUrl)
  }
  if (services.drafts) {
    provide(draftStoreKey, services.drafts)
  }
}
