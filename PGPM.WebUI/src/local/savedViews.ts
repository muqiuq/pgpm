import type { ViewState } from '@/composables/useLocalTable'
import { jsonEqual } from './outbox'
import type { Repo } from './repo'
import type { ItemType, SavedViewRow } from './rows'

/** The lists that have saved views: the item types and the gate decisions (data model §7.8). */
export type ViewType = ItemType | 'gate'

/**
 * Saved list views (docs/01 §4.3, data model §7.8): the current search, filters, sort and columns under a name –
 * personal, or shared with everybody (*Configuration: Manage*). The device has its own views and the shared ones; they
 * are written through the repository (offline too). A shared view is changed by its owner, who needs Configuration:
 * Manage (the server checks the same).
 */

export interface ViewUser {
  userId: string | null
  /** Configuration: Manage. */
  configManage: boolean
}

/** The views of a list type: own and shared, the shared ones first, then by order and name. */
export function viewsFor(rows: readonly SavedViewRow[], type: ViewType, userId: string | null): SavedViewRow[] {
  return rows
    .filter((v) => !v.deleted && v.itemType === type && (v.isShared || v.ownerUserId === userId))
    .sort(
      (a, b) => Number(b.isShared) - Number(a.isShared) || a.sortOrder - b.sortOrder || a.name.localeCompare(b.name),
    )
}

/** Whether the user may change (update, rename, delete) a view. */
export function canChange(view: SavedViewRow, user: ViewUser): boolean {
  return view.ownerUserId === user.userId && (!view.isShared || user.configManage)
}

/** A view's name: 1–100 characters (data model §7.8). */
export function viewNameError(name: string): string | null {
  const text = name.trim()
  return !text ? 'A name is needed.' : text.length > 100 ? 'At most 100 characters.' : null
}

/** The definition as stored: search, filters, sort and columns, nothing empty left undefined. */
export function definitionOf(state: ViewState): Required<ViewState> {
  return {
    search: (state.search ?? '').trim(),
    filters: { ...(state.filters ?? {}) },
    sort: [...(state.sort ?? [])],
    columns: [...(state.columns ?? [])],
  }
}

/** Whether the list shows what the view keeps (else it is *modified*). Columns count only if the view has any. */
export function sameDefinition(view: ViewState, current: ViewState): boolean {
  const a = definitionOf(view)
  const b = definitionOf(current)
  return (
    a.search === b.search &&
    jsonEqual(a.filters, b.filters) &&
    jsonEqual(a.sort, b.sort) &&
    (!a.columns.length || jsonEqual(a.columns, b.columns))
  )
}

export async function createView(
  repo: Repo,
  input: { type: ViewType; name: string; state: ViewState; shared: boolean },
  user: ViewUser,
  existing: readonly SavedViewRow[],
): Promise<SavedViewRow> {
  const error = viewNameError(input.name)
  if (error) throw new Error(error)
  if (input.shared && !user.configManage) throw new Error('Shared views need Configuration: Manage.')
  const own = existing.filter((v) => !v.deleted && v.itemType === input.type)
  const sortOrder = Math.max(0, ...own.map((v) => v.sortOrder ?? 0)) + 10
  return repo.create<SavedViewRow>('saved_views', {
    itemType: input.type,
    name: input.name.trim(),
    definition: definitionOf(input.state),
    isShared: input.shared,
    sortOrder,
  })
}

function check(view: SavedViewRow, user: ViewUser) {
  if (!canChange(view, user)) {
    throw new Error(
      view.isShared ? 'Shared views are changed by their owner with Configuration: Manage.' : 'Not yours.',
    )
  }
}

/** The view keeps the current state. */
export async function updateView(repo: Repo, view: SavedViewRow, state: ViewState, user: ViewUser): Promise<void> {
  check(view, user)
  await repo.patch('saved_views', view.id, { definition: definitionOf(state) })
}

export async function renameView(repo: Repo, view: SavedViewRow, name: string, user: ViewUser): Promise<void> {
  check(view, user)
  const error = viewNameError(name)
  if (error) throw new Error(error)
  await repo.patch('saved_views', view.id, { name: name.trim() })
}

export async function deleteView(repo: Repo, view: SavedViewRow, user: ViewUser): Promise<void> {
  check(view, user)
  await repo.remove('saved_views', view.id)
}
