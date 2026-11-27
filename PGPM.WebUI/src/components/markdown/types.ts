/**
 * The types the Markdown components share with the views built on them (docs/03 §18; PNMTD
 * docs/locations-knowledge-plan.md §5). Kept apart from the components so a view can import them without loading an
 * editor. The services the components take from the app (FileSink, FileUrlResolver, DraftStore) are in services.ts.
 */

/** A file the text uses, from the synced `files` rows (docs/02 `files`, docs/03 §18.4). */
export interface MarkdownFile {
  id: string
  name: string
  size: number
  contentType: string
  kind?: 'image' | 'attachment'
  /** `pending`: stored on this device, not uploaded yet (shown as *Pending upload*); `available`: on the server. */
  status?: 'pending' | 'available'
  /** Gone (deleted, or no longer shared): the text shows *File removed*. */
  deleted?: boolean
}

/** Which remembered editor mode applies (docs/03 §18.2): descriptions (`page`) and fields keep their own. */
export type EditorModeKey = 'page' | 'field'

/** The two editors behind the switch (PNMTD §5.4). */
export type { EditorMode } from '@/stores/preferences'

/** A revision as the synced row has it (`rev`). */
export type Revision = number | string

/**
 * A save's content, from Save, Ctrl/⌘ S or the conflict's *Save mine anyway* (docs/03 §18.5). The view saves it as
 * one patch `{ field: { old: base, new: markdown } }` through the local repo.
 */
export interface MarkdownSave {
  markdown: string
  /**
   * The text the edit started from: the patch's `old`. A restored draft's when it began on another revision, so that
   * the sync sees the conflict instead of overwriting; for *Save mine anyway* their text.
   */
  base: string
  /** The revision the edit started from (the editor's baseRevision, a restored draft's, or the conflict's). */
  revision: Revision | null
  /** *Save mine anyway* after a conflict: on top of theirs. */
  overwrite: boolean
}

/**
 * Someone else changed the text while it was being edited (docs/03 §18.5): set by the view when the synced field
 * changes under the open editor, or when a push returned a conflict for it.
 */
export interface EditConflict {
  /** Who changed it, and when (ISO 8601). */
  by: string
  at: string
  /** Their Markdown, for *Their changes*, *Take theirs* and as the `old` of *Save mine anyway*. */
  theirs: string
  /** The Markdown the edit started from; the text the editor was opened with when left out. */
  base?: string
  /** Their revision, if the view knows it: *Save mine anyway* carries it. */
  revision?: Revision
}

/** A link *Link to an item…* inserts: `[label](href)`, e.g. `[P-0042 Website relaunch](/projects/…)`. */
export interface ItemLink {
  label: string
  href: string
}
