import type { ShareSpec } from '@/api/sync'
import { keyBetween } from '@/domain/fractional'
import { withoutLocal, type PgpmDb } from './db'
import { inTransaction, type Repo, type RepoTransaction } from './repo'
import {
  allowsAccess,
  type AssignmentRow,
  type FileRow,
  type ItemLinkRow,
  type ItemRow,
  type ItemShareRow,
  type ItemType,
  type LinkRelation,
  type StatusRow,
  type Stored,
} from './rows'

/**
 * The item rules the views share (docs/01 §4–§10), on the local data: statuses and their aging, creating an item with
 * its shares in one batch, assignments and links (duplicates merged before the server has to), the bulk actions and
 * the idea/lead → project conversion. Everything goes through the repository – offline, one outbox batch per action.
 */

const day = 24 * 60 * 60 * 1000

// --- statuses (docs/01 §5) -----------------------------------------------------------------------------------------

/** A type's statuses in their order (archived ones too: they are still displayed). */
export function statusesOf(all: readonly StatusRow[], type: ItemType): StatusRow[] {
  return all
    .filter((s) => s.itemType === type && !s.deleted)
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))
}

/** The status a new item of the type gets: the default, else the first that isn't archived. */
export function defaultStatus(all: readonly StatusRow[], type: ItemType): StatusRow | undefined {
  const list = statusesOf(all, type).filter((s) => !s.isArchived)
  return list.find((s) => s.isDefault) ?? list[0]
}

/** The statuses one may pick for an item: the type's not archived ones, and the item's current one. */
export function selectableStatuses(all: readonly StatusRow[], type: ItemType, current?: string | null): StatusRow[] {
  return statusesOf(all, type).filter((s) => !s.isArchived || s.id === current)
}

/** Whole days since the status was set (`status_changed_at`; an item not synced yet: since it was created). */
export function daysInStatus(item: Pick<ItemRow, 'type' | 'statusChangedAt' | 'createdAt'>, now = Date.now()) {
  if (item.type === 'note') return null
  const since = Date.parse(item.statusChangedAt ?? item.createdAt)
  if (Number.isNaN(since)) return null
  return Math.max(0, Math.floor((now - since) / day))
}

/** The ISO date of today in the browser's zone (`2026-11-27`). */
export function today(now = Date.now()): string {
  const d = new Date(now)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** Why an item is overdue (docs/01 §5, §9): longer in its status than the status allows, or a lead's next action due. */
export function overdueReason(
  item: Pick<ItemRow, 'type' | 'statusChangedAt' | 'createdAt' | 'leadNextActionDue'>,
  status: StatusRow | undefined,
  now = Date.now(),
): string | null {
  if (item.type === 'note' || status?.category === 'closed') return null
  const days = daysInStatus(item, now)
  if (status?.maxDays != null && days !== null && days > status.maxDays) {
    return `${days} days in ${status.name} (at most ${status.maxDays})`
  }
  if (item.type === 'lead' && item.leadNextActionDue && item.leadNextActionDue < today(now)) {
    return `Next action was due ${item.leadNextActionDue}`
  }
  return null
}

export function isOverdue(
  item: Pick<ItemRow, 'type' | 'statusChangedAt' | 'createdAt' | 'leadNextActionDue'>,
  status: StatusRow | undefined,
  now = Date.now(),
): boolean {
  return overdueReason(item, status, now) !== null
}

// --- creating --------------------------------------------------------------------------------------------------------

export interface NewItem {
  type: ItemType
  title: string
  /** *Share with* of the create dialog (docs/01 §4.2): inserted with the item, in its batch. */
  shares: readonly ShareSpec[]
  /** Ideas, leads, projects: required (the default status of the type, unless chosen). */
  statusId?: string | null
  /** Further columns (lead and project fields, origin, colour, image). */
  values?: Partial<ItemRow>
}

/** The title rules of the server (1–300 characters); null if fine. */
export function titleError(title: string): string | null {
  const text = title.trim()
  return !text ? 'A title is needed.' : text.length > 300 ? 'At most 300 characters.' : null
}

/** Creates an item and its shares inside the caller's transaction (one batch). */
export function createItem(tx: RepoTransaction, input: NewItem): Promise<Stored<ItemRow>> {
  return inTransaction(create(tx, input))
}

async function create(tx: RepoTransaction, input: NewItem): Promise<Stored<ItemRow>> {
  const error = titleError(input.title)
  if (error) throw new Error(error)
  if (input.type !== 'note' && !input.statusId) throw new Error('There is no status for this type yet.')
  const row: Partial<ItemRow> = {
    ...input.values,
    type: input.type,
    title: input.title.trim(),
    statusId: input.type === 'note' ? null : (input.statusId ?? null),
  }
  if (input.type === 'lead' && row.leadCurrency === undefined) row.leadCurrency = 'CHF'
  const item = await tx.create<ItemRow>('items', row)
  for (const share of input.shares) {
    await tx.create('item_shares', { itemId: item.id, groupId: share.groupId, access: share.access })
  }
  return item
}

// --- assignments (docs/01 §4.6) --------------------------------------------------------------------------------------

/** The live assignments of an item, in their order. */
export function assignmentsOf(db: PgpmDb, itemId: string): Promise<Stored<AssignmentRow>[]> {
  return inTransaction(assignments(db, itemId))
}

async function assignments(db: PgpmDb, itemId: string): Promise<Stored<AssignmentRow>[]> {
  const rows = (await db.synced('assignments').where('itemId').equals(itemId).toArray()) as unknown as Stored<
    AssignmentRow[]
  >
  return rows
    .filter((a) => !a.deleted)
    .sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : a.sortOrder > b.sortOrder ? 1 : a.id < b.id ? -1 : 1))
}

/**
 * Assigns a person to an item (once per item: an existing assignment gets the role instead). Answers what it did.
 */
export function assignPerson(
  tx: RepoTransaction,
  itemId: string,
  personId: string,
  role: string | null,
): Promise<'added' | 'updated' | 'unchanged'> {
  return inTransaction(assign(tx, itemId, personId, role))
}

async function assign(
  tx: RepoTransaction,
  itemId: string,
  personId: string,
  role: string | null,
): Promise<'added' | 'updated' | 'unchanged'> {
  const list = await assignmentsOf(tx.db, itemId)
  const existing = list.find((a) => a.personId === personId)
  const cleaned = role?.trim() || null
  if (existing) {
    if ((existing.role ?? null) === cleaned || cleaned === null) return 'unchanged'
    await tx.patch('assignments', existing.id, { role: cleaned })
    return 'updated'
  }
  const last = list.length ? list[list.length - 1]!.sortOrder : null
  await tx.create<AssignmentRow>('assignments', {
    itemId,
    milestoneId: null,
    personId,
    role: cleaned,
    sortOrder: keyBetween(last, null),
  })
  return 'added'
}

/** The roles suggested when none are configured (docs/01 §4.1). */
export const defaultRoles = [
  'Responsible',
  'Contact',
  'Contributor',
  'Innovation team',
  'Review board',
  'Domain expert',
  'Delivery team',
  'Sponsor',
  'Data owner',
  'AI champion',
]

// --- links (docs/01 §4.7) --------------------------------------------------------------------------------------------

export const relations: readonly { value: LinkRelation; label: string; inverse: string }[] = [
  { value: 'contributes_to', label: 'contributes to', inverse: 'contributed to by' },
  { value: 'learning_of', label: 'is learning of', inverse: 'has learning' },
  { value: 'depends_on', label: 'depends on', inverse: 'is needed by' },
  { value: 'related', label: 'related to', inverse: 'related to' },
]

/** A link from one item to another, unless the same one exists (the server would merge it anyway). */
export function addLink(
  tx: RepoTransaction,
  fromItemId: string,
  toItemId: string,
  relation: LinkRelation,
): Promise<boolean> {
  return inTransaction(link(tx, fromItemId, toItemId, relation))
}

async function link(
  tx: RepoTransaction,
  fromItemId: string,
  toItemId: string,
  relation: LinkRelation,
): Promise<boolean> {
  if (fromItemId === toItemId) throw new Error('An item can’t be linked to itself.')
  const existing = (
    (await tx.db.synced('item_links').where('fromItemId').equals(fromItemId).toArray()) as unknown as ItemLinkRow[]
  ).some((l) => !l.deleted && l.toItemId === toItemId && l.relation === relation)
  if (existing) return false
  await tx.create<ItemLinkRow>('item_links', { fromItemId, toItemId, relation })
  return true
}

// --- bulk actions (docs/01 §4.3) -------------------------------------------------------------------------------------

export interface BulkResult {
  changed: number
  /** Not changed: read-only, gone meanwhile, or nothing to change. */
  skipped: number
}

/** The same change on several items, one batch; items without *edit* access are skipped. */
export async function bulkPatch(repo: Repo, ids: readonly string[], values: Partial<ItemRow>): Promise<BulkResult> {
  return repo.transaction(async (tx) => {
    const result: BulkResult = { changed: 0, skipped: 0 }
    for (const id of ids) {
      const item = (await tx.db.synced('items').get(id)) as Stored<ItemRow> | undefined
      if (!item || item.deleted || !allowsAccess(item.access, 'edit')) {
        result.skipped++
        continue
      }
      // A status of another type (a mixed selection) doesn't fit.
      if (values.statusId !== undefined && item.type === 'note') {
        result.skipped++
        continue
      }
      await tx.patch('items', id, values as Record<string, unknown>)
      result.changed++
    }
    return result
  })
}

/** Assigns one person (with a role) to several items, one batch. */
export async function bulkAssign(
  repo: Repo,
  ids: readonly string[],
  personId: string,
  role: string | null,
): Promise<BulkResult> {
  return repo.transaction(async (tx) => {
    const result: BulkResult = { changed: 0, skipped: 0 }
    for (const id of ids) {
      const item = (await tx.db.synced('items').get(id)) as Stored<ItemRow> | undefined
      if (!item || item.deleted || !allowsAccess(item.access, 'edit')) {
        result.skipped++
        continue
      }
      if ((await assignPerson(tx, id, personId, role)) === 'unchanged') result.skipped++
      else result.changed++
    }
    return result
  })
}

/** Deletes several items (to the Trash), one batch; items without *manage* access are skipped. */
export async function bulkRemove(repo: Repo, ids: readonly string[]): Promise<BulkResult> {
  return repo.transaction(async (tx) => {
    const result: BulkResult = { changed: 0, skipped: 0 }
    for (const id of ids) {
      const item = (await tx.db.synced('items').get(id)) as Stored<ItemRow> | undefined
      if (!item || item.deleted || !allowsAccess(item.access, 'manage')) {
        result.skipped++
        continue
      }
      await tx.remove('items', id)
      result.changed++
    }
    return result
  })
}

// --- conversion (docs/01 §8, §9; docs/03 §12.8) ----------------------------------------------------------------------

export interface ConversionOptions {
  /** The project's title; default the source's. */
  title?: string
  /** The project's status; default the project default. */
  statusId?: string | null
  /** Phase 4: the template, its sections and field values. */
  templateId?: string | null
}

export interface Conversion {
  project: Stored<ItemRow>
  /** The source's new status (*Selected*, *Won*), if one was found. */
  sourceStatus: StatusRow | null
  copied: { shares: number; assignments: number; links: number; files: number }
}

/** The project the item was converted into: the server's `convertedToItemId`, or a local project naming it as origin. */
export function convertedProject(db: PgpmDb, source: ItemRow): Promise<Stored<ItemRow> | undefined> {
  return inTransaction(converted(db, source))
}

async function converted(db: PgpmDb, source: ItemRow): Promise<Stored<ItemRow> | undefined> {
  if (source.convertedToItemId) {
    const project = (await db.synced('items').get(source.convertedToItemId)) as Stored<ItemRow> | undefined
    if (project && !project.deleted) return project
  }
  const local = (await db.synced('items').where('originItemId').equals(source.id).toArray()) as unknown as Stored<
    ItemRow[]
  >
  return local.find((p) => !p.deleted && p.type === 'project')
}

/** The status an idea or lead moves to when converted: *Selected* (ideas), *Won* (leads), if the type has it. */
export function convertedStatus(all: readonly StatusRow[], type: ItemType): StatusRow | null {
  const name = type === 'idea' ? 'selected' : type === 'lead' ? 'won' : null
  if (!name) return null
  return statusesOf(all, type).find((s) => !s.isArchived && s.name.trim().toLowerCase() === name) ?? null
}

/**
 * The source's cover and attachments as files of the project (docs/01 §8, docs/03 §12.8): new `files` rows with the same
 * name, type, size and SHA-256 – the server shares the stored bytes of an available file the user can read, so nothing
 * is uploaded again (docs/03 §10.3) – and the bytes on this device copied along (an upload still waiting goes up for the
 * copy too). A file whose bytes are only on another device and not uploaded yet stays on the source. Answers the
 * cover's copy, if any, and how many files were copied.
 */
function copyFiles(
  tx: RepoTransaction,
  source: ItemRow,
  projectId: string,
): Promise<{ cover: string | null; files: number }> {
  return inTransaction(copyEach(tx, source, projectId))
}

async function copyEach(
  tx: RepoTransaction,
  source: ItemRow,
  projectId: string,
): Promise<{ cover: string | null; files: number }> {
  const rows = (
    (await tx.db.synced('files').where('itemId').equals(source.id).toArray()) as unknown as Stored<FileRow>[]
  ).filter((f) => !f.deleted && (f.purpose === 'attachment' || f.id === source.imageFileId))
  rows.sort((a, b) => a.createdAt.localeCompare(b.createdAt) || (a.id < b.id ? -1 : 1))
  let cover: string | null = null
  let files = 0
  for (const file of rows) {
    const bytes = await tx.db.blobs.get(file.id)
    if (file.status !== 'available' && !bytes) continue
    const copy = await tx.create<FileRow>('files', {
      itemId: projectId,
      mapId: null,
      purpose: file.purpose,
      fileName: file.fileName,
      contentType: file.contentType,
      sizeBytes: file.sizeBytes,
      sha256: file.sha256,
      width: file.width,
      height: file.height,
      thumbnail: file.thumbnail,
    })
    if (bytes) {
      // The server has the source's bytes: the copy shares them at its insert, nothing to upload.
      const uploaded = file.status === 'available'
      await tx.db.blobs.put({
        ...bytes,
        id: copy.id,
        uploaded,
        nextChunk: 0,
        error: null,
        cachedAt: new Date().toISOString(),
      })
    }
    if (file.id === source.imageFileId) cover = copy.id
    files++
  }
  return { cover, files }
}

/**
 * Converts an idea or lead into a project, on the device (offline too), in one batch: a new project with the source's
 * title, description and colour, its cover image and attachments (copies sharing the stored bytes, {@link copyFiles}),
 * its shares, its assignments and its outgoing links, `originItemId` = the source (the server sets the source's
 * `convertedToItemId`), a `related` link from the project to the source, and the source moved to *Selected* / *Won*
 * where such a status exists. Needs *edit* on the source (its status changes). The cover is set by a patch after the
 * copy's insert: the server takes only an image file of the project itself as its `imageFileId`.
 */
export async function convertToProject(
  repo: Repo,
  sourceId: string,
  statuses: readonly StatusRow[],
  options: ConversionOptions = {},
): Promise<Conversion> {
  return repo.transaction(async (tx) => {
    const db = tx.db
    const source = (await db.synced('items').get(sourceId)) as Stored<ItemRow> | undefined
    if (!source || source.deleted) throw new Error('The item is not on this device (any more).')
    if (source.type !== 'idea' && source.type !== 'lead') throw new Error('Only ideas and leads are converted.')
    if (!allowsAccess(source.access, 'edit')) throw new Error('You can read this item, not convert it.')
    if (await convertedProject(db, source)) throw new Error('It was converted already.')
    const statusId = options.statusId ?? defaultStatus(statuses, 'project')?.id
    if (!statusId) throw new Error('There is no project status yet.')

    const shares = (
      (await db.synced('item_shares').where('itemId').equals(sourceId).toArray()) as unknown as ItemShareRow[]
    ).filter((s) => !s.deleted)
    const project = await createItem(tx, {
      type: 'project',
      title: options.title?.trim() || source.title,
      statusId,
      shares: shares.map((s) => ({ groupId: s.groupId, access: s.access })),
      values: {
        description: source.description,
        color: source.color,
        originItemId: source.id,
        projectTemplateId: options.templateId ?? null,
      },
    })
    const copies = await copyFiles(tx, source, project.id)
    if (copies.cover) await tx.patch('items', project.id, { imageFileId: copies.cover })

    let assignments = 0
    for (const a of await assignmentsOf(db, sourceId)) {
      if ((await assignPerson(tx, project.id, a.personId, a.role)) === 'added') assignments++
    }

    let links = 0
    const outgoing = (
      (await db.synced('item_links').where('fromItemId').equals(sourceId).toArray()) as unknown as ItemLinkRow[]
    ).filter((l) => !l.deleted)
    for (const link of outgoing) {
      if (link.toItemId !== project.id && (await addLink(tx, project.id, link.toItemId, link.relation))) links++
    }
    await addLink(tx, project.id, sourceId, 'related')

    const next = convertedStatus(statuses, source.type)
    if (next && next.id !== source.statusId) await tx.patch('items', sourceId, { statusId: next.id })
    const stored = (await db.synced('items').get(project.id)) ?? project
    return {
      project: withoutLocal(stored) as Stored<ItemRow>,
      sourceStatus: next,
      copied: { shares: shares.length, assignments, links, files: copies.files },
    }
  })
}
