import { compareKeyThenId, keyBetween, keysForOrder } from '@/domain/fractional'
import { compareMilestones, dodStateError } from '@/domain/milestones'
import type { PgpmDb } from './db'
import { inTransaction, type Repo, type RepoTransaction } from './repo'
import {
  allowsAccess,
  type AssignmentRow,
  type DodItemRow,
  type DodState,
  type GateDecisionValue,
  type ItemRow,
  type MilestoneKind,
  type MilestoneRow,
  type Stored,
} from './rows'

/**
 * A project's milestones and gates on the device (docs/01 §11, data model §8): created, changed, ordered and deleted
 * through the repository – offline, one outbox batch per action. The Definition of Done's criteria, the gate decision
 * (decision and note in one patch; the server sets who and when), the status the decision suggests, and the persons
 * responsible (assignments with `milestoneId`). Rows of a project the user may only read are refused here already.
 */

/** The role a person assigned to a gate gets unless another is given: the deciders (docs/01 §11). */
export const GATE_ROLE = 'Review board'

function byKey<T extends { sortOrder: string; id: string }>(a: T, b: T) {
  return compareKeyThenId(a.sortOrder, a.id, b.sortOrder, b.id)
}

/** The live milestones of a project, in their order. */
export function milestonesOf(db: PgpmDb, itemId: string): Promise<Stored<MilestoneRow>[]> {
  return inTransaction(readMilestones(db, itemId))
}

async function readMilestones(db: PgpmDb, itemId: string): Promise<Stored<MilestoneRow>[]> {
  const rows = (await db.synced('milestones').where('itemId').equals(itemId).toArray()) as unknown as Stored<
    MilestoneRow[]
  >
  return rows.filter((m) => !m.deleted).sort(compareMilestones)
}

/** The live DoD criteria of a milestone, in their order. */
export function dodItemsOf(db: PgpmDb, milestoneId: string): Promise<Stored<DodItemRow>[]> {
  return inTransaction(readDod(db, milestoneId))
}

async function readDod(db: PgpmDb, milestoneId: string): Promise<Stored<DodItemRow>[]> {
  const rows = (await db.synced('dod_items').where('milestoneId').equals(milestoneId).toArray()) as unknown as Stored<
    DodItemRow[]
  >
  return rows.filter((d) => !d.deleted).sort(byKey)
}

/** The live persons assigned to a milestone, in their order. */
export function milestoneAssignmentsOf(db: PgpmDb, milestoneId: string): Promise<Stored<AssignmentRow>[]> {
  return inTransaction(readAssignments(db, milestoneId))
}

async function readAssignments(db: PgpmDb, milestoneId: string): Promise<Stored<AssignmentRow>[]> {
  const rows = (await db.synced('assignments').where('milestoneId').equals(milestoneId).toArray()) as unknown as Stored<
    AssignmentRow[]
  >
  return rows.filter((a) => !a.deleted).sort(byKey)
}

/** The project, if the user may edit it (milestones need *edit* on their project). */
function editableProject(tx: RepoTransaction, itemId: string): Promise<Stored<ItemRow>> {
  return inTransaction(checkProject(tx, itemId))
}

async function checkProject(tx: RepoTransaction, itemId: string): Promise<Stored<ItemRow>> {
  const item = (await tx.db.synced('items').get(itemId)) as Stored<ItemRow> | undefined
  if (!item || item.deleted) throw new Error('The project is not on this device (any more).')
  if (item.type !== 'project') throw new Error('Milestones belong to projects.')
  if (!allowsAccess(item.access, 'edit')) throw new Error('You can read this project, not change it.')
  return item
}

function editableMilestone(tx: RepoTransaction, milestoneId: string): Promise<Stored<MilestoneRow>> {
  return inTransaction(checkMilestone(tx, milestoneId))
}

async function checkMilestone(tx: RepoTransaction, milestoneId: string): Promise<Stored<MilestoneRow>> {
  const milestone = (await tx.db.synced('milestones').get(milestoneId)) as Stored<MilestoneRow> | undefined
  if (!milestone || milestone.deleted) throw new Error('The milestone was deleted meanwhile.')
  await editableProject(tx, milestone.itemId)
  return milestone
}

export function titleError(title: string): string | null {
  const text = title.trim()
  return !text ? 'A title is needed.' : text.length > 300 ? 'At most 300 characters.' : null
}

export interface NewMilestone {
  kind: MilestoneKind
  title: string
  dueDate?: string | null
  description?: string | null
  color?: string | null
  /** The milestone it goes after; undefined: at the end, null: at the start. */
  after?: string | null
  /** Criteria of its Definition of Done, in order. */
  dod?: readonly string[]
  /** Gates: the statuses suggested on go / no-go. */
  onGoStatusId?: string | null
  onNoGoStatusId?: string | null
  /** Gates: the decision it starts with (default *pending*). */
  decision?: GateDecisionValue | null
}

/** Adds a milestone or gate (and its DoD criteria) to a project inside the caller's transaction. */
export function addMilestone(tx: RepoTransaction, itemId: string, input: NewMilestone): Promise<Stored<MilestoneRow>> {
  return inTransaction(add(tx, itemId, input))
}

async function add(tx: RepoTransaction, itemId: string, input: NewMilestone): Promise<Stored<MilestoneRow>> {
  const error = titleError(input.title)
  if (error) throw new Error(error)
  await editableProject(tx, itemId)
  const list = await milestonesOf(tx.db, itemId)
  let before: string | null
  let after: string | null
  if (input.after === undefined) {
    before = list.length ? list[list.length - 1]!.sortOrder : null
    after = null
  } else if (input.after === null) {
    before = null
    after = list[0]?.sortOrder ?? null
  } else {
    const at = list.findIndex((m) => m.id === input.after)
    before = at >= 0 ? list[at]!.sortOrder : list.length ? list[list.length - 1]!.sortOrder : null
    after = at >= 0 ? (list[at + 1]?.sortOrder ?? null) : null
  }
  const gate = input.kind === 'gate'
  const milestone = await tx.create<MilestoneRow>('milestones', {
    itemId,
    kind: input.kind,
    title: input.title.trim(),
    description: input.description ?? null,
    dueDate: input.dueDate || null,
    status: 'planned',
    decision: gate ? (input.decision ?? 'pending') : null,
    onGoStatusId: gate ? (input.onGoStatusId ?? null) : null,
    onNoGoStatusId: gate ? (input.onNoGoStatusId ?? null) : null,
    color: input.color ?? null,
    sortOrder: keyBetween(before, before !== null && after !== null && after <= before ? null : after),
  })
  let last: string | null = null
  for (const text of input.dod ?? []) {
    if (!text.trim()) continue
    last = keyBetween(last, null)
    await tx.create<DodItemRow>('dod_items', {
      milestoneId: milestone.id,
      text: text.trim(),
      state: 'open',
      sortOrder: last,
    })
  }
  return milestone
}

/** Adds a milestone in a batch of its own. */
export function createMilestone(repo: Repo, itemId: string, input: NewMilestone): Promise<Stored<MilestoneRow>> {
  return repo.transaction((tx) => addMilestone(tx, itemId, input))
}

/** The members a client changes (docs/03 §11.5 whitelist); the server sets `completedAt`, `decidedAt`, `decidedBy`. */
export type MilestoneChange = Partial<
  Pick<
    MilestoneRow,
    | 'title'
    | 'description'
    | 'dueDate'
    | 'status'
    | 'decision'
    | 'decisionNote'
    | 'color'
    | 'onGoStatusId'
    | 'onNoGoStatusId'
  >
>

export async function updateMilestone(repo: Repo, milestoneId: string, change: MilestoneChange): Promise<void> {
  if (change.title !== undefined) {
    const error = titleError(change.title)
    if (error) throw new Error(error)
    change = { ...change, title: change.title.trim() }
  }
  await repo.transaction(async (tx) => {
    const milestone = await editableMilestone(tx, milestoneId)
    if (milestone.kind !== 'gate' && (change.decision !== undefined || change.decisionNote !== undefined))
      throw new Error('Only gates have a decision.')
    await tx.patch('milestones', milestoneId, change as Record<string, unknown>)
  })
}

/** Records a gate decision with its note, one patch (the server sets who and when). */
export async function decideGate(
  repo: Repo,
  milestoneId: string,
  decision: GateDecisionValue,
  note: string | null,
): Promise<void> {
  const text = note?.trim() || null
  if (text && text.length > 4000) throw new Error('The note has at most 4,000 characters.')
  await updateMilestone(repo, milestoneId, { decision, decisionNote: text })
}

/** Moves the project to the status a gate decision suggested (the user confirmed it). */
export async function applySuggestedStatus(repo: Repo, itemId: string, statusId: string): Promise<void> {
  await repo.transaction(async (tx) => {
    const item = await editableProject(tx, itemId)
    if (item.statusId !== statusId) await tx.patch('items', itemId, { statusId })
  })
}

/** Deletes a milestone; its DoD criteria and assignments go with it on the server (same delete batch). */
export async function deleteMilestone(repo: Repo, milestoneId: string): Promise<void> {
  await repo.transaction(async (tx) => {
    await editableMilestone(tx, milestoneId)
    for (const d of await dodItemsOf(tx.db, milestoneId)) await tx.remove('dod_items', d.id)
    for (const a of await milestoneAssignmentsOf(tx.db, milestoneId)) await tx.remove('assignments', a.id)
    await tx.remove('milestones', milestoneId)
  })
}

/** Moves a milestone one up (-1) or down (+1); only the rows that need a new key are patched. */
export async function moveMilestone(repo: Repo, itemId: string, milestoneId: string, step: -1 | 1): Promise<boolean> {
  return repo.transaction(async (tx) => {
    await editableProject(tx, itemId)
    const list = await milestonesOf(tx.db, itemId)
    return reorder(tx, 'milestones', list, milestoneId, step)
  })
}

function reorder(
  tx: RepoTransaction,
  table: string,
  list: readonly { id: string; sortOrder: string }[],
  id: string,
  step: -1 | 1,
): Promise<boolean> {
  return inTransaction(move(tx, table, list, id, step))
}

async function move(
  tx: RepoTransaction,
  table: string,
  list: readonly { id: string; sortOrder: string }[],
  id: string,
  step: -1 | 1,
): Promise<boolean> {
  const at = list.findIndex((r) => r.id === id)
  const to = at + step
  if (at < 0 || to < 0 || to >= list.length) return false
  const desired = list.map((r) => ({ id: r.id, key: r.sortOrder }))
  const [moved] = desired.splice(at, 1)
  desired.splice(to, 0, moved!)
  for (const [rowId, key] of keysForOrder(desired, new Set([id]))) await tx.patch(table, rowId, { sortOrder: key })
  return true
}

// --- Definition of Done (docs/01 §11, docs/04 F7) ----------------------------------------------------------------

function dodTextError(text: string): string | null {
  const t = text.trim()
  return !t ? 'A criterion is needed.' : t.length > 500 ? 'At most 500 characters.' : null
}

export async function addDodItem(repo: Repo, milestoneId: string, text: string): Promise<Stored<DodItemRow>> {
  const error = dodTextError(text)
  if (error) throw new Error(error)
  return repo.transaction(async (tx) => {
    await editableMilestone(tx, milestoneId)
    const list = await dodItemsOf(tx.db, milestoneId)
    return tx.create<DodItemRow>('dod_items', {
      milestoneId,
      text: text.trim(),
      state: 'open',
      sortOrder: keyBetween(list.length ? list[list.length - 1]!.sortOrder : null, null),
    })
  })
}

function editableDod(tx: RepoTransaction, dodId: string): Promise<Stored<DodItemRow>> {
  return inTransaction(checkDod(tx, dodId))
}

async function checkDod(tx: RepoTransaction, dodId: string): Promise<Stored<DodItemRow>> {
  const item = (await tx.db.synced('dod_items').get(dodId)) as Stored<DodItemRow> | undefined
  if (!item || item.deleted) throw new Error('The criterion was deleted meanwhile.')
  await editableMilestone(tx, item.milestoneId)
  return item
}

/** A criterion's state and note (*n/a* needs a note, like the server); the note may change alone. */
export async function setDodState(repo: Repo, dodId: string, state: DodState, note?: string | null): Promise<void> {
  await repo.transaction(async (tx) => {
    const item = await editableDod(tx, dodId)
    const nextNote = note === undefined ? item.note : note?.trim() || null
    const error = dodStateError(state, nextNote)
    if (error) throw new Error(error)
    if (nextNote && nextNote.length > 2000) throw new Error('The note has at most 2,000 characters.')
    await tx.patch('dod_items', dodId, { state, note: nextNote })
  })
}

export async function renameDodItem(repo: Repo, dodId: string, text: string): Promise<void> {
  const error = dodTextError(text)
  if (error) throw new Error(error)
  await repo.transaction(async (tx) => {
    await editableDod(tx, dodId)
    await tx.patch('dod_items', dodId, { text: text.trim() })
  })
}

export async function removeDodItem(repo: Repo, dodId: string): Promise<void> {
  await repo.transaction(async (tx) => {
    await editableDod(tx, dodId)
    await tx.remove('dod_items', dodId)
  })
}

export async function moveDodItem(repo: Repo, dodId: string, step: -1 | 1): Promise<boolean> {
  return repo.transaction(async (tx) => {
    const item = await editableDod(tx, dodId)
    return reorder(tx, 'dod_items', await dodItemsOf(tx.db, item.milestoneId), dodId, step)
  })
}

// --- responsible persons (assignments with milestoneId) ----------------------------------------------------------

/**
 * Assigns a person to a milestone, once (assigning again changes the role). A gate's persons are its deciders: role
 * *Review board* unless another is given. Answers what it did.
 */
export async function assignToMilestone(
  repo: Repo,
  milestoneId: string,
  personId: string,
  role?: string | null,
): Promise<'added' | 'updated' | 'unchanged'> {
  return repo.transaction(async (tx) => {
    const milestone = await editableMilestone(tx, milestoneId)
    const list = await milestoneAssignmentsOf(tx.db, milestoneId)
    const cleaned = role === undefined ? (milestone.kind === 'gate' ? GATE_ROLE : null) : role?.trim() || null
    const existing = list.find((a) => a.personId === personId)
    if (existing) {
      if (cleaned === null || (existing.role ?? null) === cleaned) return 'unchanged'
      await tx.patch('assignments', existing.id, { role: cleaned })
      return 'updated'
    }
    await tx.create<AssignmentRow>('assignments', {
      itemId: null,
      milestoneId,
      personId,
      role: cleaned,
      sortOrder: keyBetween(list.length ? list[list.length - 1]!.sortOrder : null, null),
    })
    return 'added'
  })
}

export async function unassignFromMilestone(repo: Repo, assignmentId: string): Promise<void> {
  await repo.transaction(async (tx) => {
    const a = (await tx.db.synced('assignments').get(assignmentId)) as Stored<AssignmentRow> | undefined
    if (!a || a.deleted || !a.milestoneId) return
    await editableMilestone(tx, a.milestoneId)
    await tx.remove('assignments', assignmentId)
  })
}
