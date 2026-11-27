/**
 * The synced rows as the client keeps them (data model §1.1, §3–§6, §13), in the wire format of the sync contract:
 * camelCase members, ISO-8601 UTC timestamps, lower-case uuid strings, jsonb as JSON. Type aliases (not interfaces), so
 * that every row is a `Row` of outbox.ts.
 */

/** An item's or map's computed access for the receiving user (data model §1.2). */
export type Access = 'read' | 'edit' | 'manage'

/** Order of the access levels: read < edit < manage. */
export const accessRank: Record<Access, number> = { read: 1, edit: 2, manage: 3 }

/** Whether `held` includes `needed`. */
export function allowsAccess(held: Access | null | undefined, needed: Access): boolean {
  return !!held && accessRank[held] >= accessRank[needed]
}

/** The columns every synced row carries (data model §1.1). */
export type SyncedColumns = {
  id: string
  seq: number
  rev: number
  deleted: boolean
  deleteBatchId: string | null
  createdAt: string
  createdBy: string | null
  updatedAt: string
  updatedBy: string | null
}

/** What the device keeps beside a row (docs/03 §11.3): never part of the row, stripped before anything leaves. */
export type LocalMeta = {
  /** The server's `rev` of the row; null for a row the server hasn't accepted yet. */
  serverRev: number | null
  /** Outbox entries for the row are waiting. */
  pending: boolean
  /**
   * While entries are pending: the row as the server has it (after the accepted ones), so a result or a fresh server
   * row can be rebased. Null for a row the server doesn't have.
   */
  base?: Record<string, unknown> | null
}

/** The member a stored row keeps its {@link LocalMeta} in. */
export const localKey = '_local'

export type ItemType = 'note' | 'idea' | 'lead' | 'project'

/** `items` (data model §6): notes, ideas, leads and projects in one table. */
export type ItemRow = SyncedColumns & {
  type: ItemType
  /** Per-type running number from the server; null until the first sync (key shown as pending). */
  number: number | null
  title: string
  description: string | null
  color: string | null
  imageFileId: string | null
  ownerUserId: string
  statusId: string | null
  statusChangedAt: string | null
  tags: string[]
  originItemId: string | null
  convertedToItemId: string | null
  pinned: boolean
  leadOrganisation: string | null
  leadValue: number | null
  leadCurrency: string | null
  leadProbability: number | null
  leadDecisionDate: string | null
  leadNextAction: string | null
  leadNextActionDue: string | null
  leadSource: string | null
  projectTemplateId: string | null
  projectStartDate: string | null
  projectEndDate: string | null
  /** Computed for the receiving user (data model §1.2). */
  access: Access
}

/** `item_shares` (data model §4): read-only after creation. */
export type ItemShareRow = SyncedColumns & {
  itemId: string
  groupId: string
  access: Access
}

/** `groups` (data model §3): read-only, names for the share picker. */
export type GroupRow = SyncedColumns & {
  name: string
  description: string | null
}

/** `persons` (data model §5). */
export type PersonRow = SyncedColumns & {
  source: 'microsoft' | 'manual'
  entraTenantId: string | null
  entraObjectId: string | null
  displayName: string
  givenName: string | null
  surname: string | null
  email: string | null
  userPrincipalName: string | null
  userType: string | null
  jobTitle: string | null
  department: string | null
  companyName: string | null
  phone: string | null
  mobilePhone: string | null
  isActive: boolean
  directorySyncedAt: string | null
  notes: string | null
  userId: string | null
}

/** `user_settings` (data model §13): the own rows only. */
export type UserSettingRow = SyncedColumns & {
  userId: string
  key: string
  value: unknown
}

export type StatusCategory = 'open' | 'active' | 'closed'

/** `statuses` (data model §7.1): read-only configuration, one list per item type. `icon`: a PrimeIcons name (`pi-eye`). */
export type StatusRow = SyncedColumns & {
  itemType: Exclude<ItemType, 'note'>
  name: string
  color: string
  icon: string | null
  category: StatusCategory
  sortOrder: number
  isDefault: boolean
  maxDays: number | null
  isArchived: boolean
}

/** `assignments` (data model §6): a person on an item (or milestone), with an optional role. */
export type AssignmentRow = SyncedColumns & {
  itemId: string | null
  milestoneId: string | null
  personId: string
  role: string | null
  /** Fractional index (data model §10). */
  sortOrder: string
}

export type LinkRelation = 'contributes_to' | 'learning_of' | 'depends_on' | 'related'

/** `item_links` (data model §6): belongs to its source item; the target may be unreadable (*restricted*). */
export type ItemLinkRow = SyncedColumns & {
  fromItemId: string
  toItemId: string
  relation: LinkRelation
}

export type FilePurpose = 'attachment' | 'cover' | 'map_image' | 'embedded'

/** `files` (data model §6): metadata; the bytes live on the server and, cached, in IndexedDB `blobs`. */
export type FileRow = SyncedColumns & {
  itemId: string | null
  mapId: string | null
  purpose: FilePurpose
  fileName: string
  contentType: string
  sizeBytes: number
  /** Hex, computed by the client, verified by the server. */
  sha256: string
  width: number | null
  height: number | null
  /** A WebP of ≤ 32 KB, base64, made by the client. */
  thumbnail: string | null
  /** `pending` until the server has the bytes (the commit). */
  status: 'pending' | 'available'
}

/** `saved_views` (data model §7.8): own views and the shared ones. */
export type SavedViewRow = SyncedColumns & {
  ownerUserId: string
  itemType: ItemType | 'gate'
  name: string
  definition: Record<string, unknown>
  isShared: boolean
  sortOrder: number
}

/** `assignment_roles` (phase 4 fills it): suggestions for `assignments.role`. */
export type AssignmentRoleRow = SyncedColumns & {
  name: string
  description?: string | null
  sortOrder: number
  isArchived: boolean
}

/** `project_templates` (data model §7.7), as far as phase 3 reads them (the create dialog's picker slot). */
export type ProjectTemplateRow = SyncedColumns & {
  name: string
  description: string | null
  color: string | null
  icon: string | null
  defaultStatusId: string | null
  defaultShares: { groupId: string; access: Access }[]
  sortOrder: number
  isArchived: boolean
}

/** A row as stored locally: the row and its bookkeeping. */
export type Stored<T> = T & { _local?: LocalMeta }

/** The display key of an item (`N-12`), or the prefix with an ellipsis while the server hasn't numbered it. */
export function itemKey(item: Pick<ItemRow, 'type' | 'number'>): string {
  const prefix = { note: 'N', idea: 'I', lead: 'L', project: 'P' }[item.type]
  if (item.number === null || item.number === undefined) return `${prefix}-…`
  return item.type === 'project' ? `${prefix}-${String(item.number).padStart(4, '0')}` : `${prefix}-${item.number}`
}
