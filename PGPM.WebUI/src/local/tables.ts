import type { Row } from './outbox'

/**
 * The synced tables the device keeps (data model §14), one entry each: a later phase adds a table with one entry here
 * and a bump of {@link dbVersion} – the Dexie schema, the eviction cascade, the reset and the write checks all come from
 * this list.
 *
 * - `name`: the wire name (snake_case table name) and the Dexie store's name.
 * - `indexes`: Dexie indexes besides the primary key `id` – row members in camelCase (`itemId`), compound ones as
 *   `[a+b]`. Booleans are no IndexedDB keys: filter on them in memory.
 * - `parents`: the rows this table's rows hang below, by which member; evicting or purging a parent removes them.
 * - `writes`: what the client may push – every operation, inserts only (`item_shares`, with a new item), or nothing
 *   (read-only configuration and `groups`).
 * - `root`: the eviction kind whose root lives in this table (`item`, `map`).
 * - `serverOwned`: members only the server sets; never pushed in an insert.
 * - `defaults`: the members a new local row starts with before the server has seen it.
 * - `blobs`: the row's id keys file bytes in `blobs` (`files`, phase 3), removed with the row.
 * - `since`: the Dexie version that added the table; `revised`: the version that last changed its indexes, `before`:
 *   the indexes until then (each version keeps the schema it had, so that an upgrade adds the new index).
 */
export interface TableDef {
  name: string
  indexes: readonly string[]
  parents: readonly { table: string; key: string }[]
  writes: 'all' | 'insert' | 'none'
  root?: 'item' | 'map'
  serverOwned?: readonly string[]
  defaults?: (context: { userId: string | null }) => Row
  blobs?: boolean
  since: number
  revised?: number
  before?: readonly string[]
}

/** The Dexie version of the schema; raise it with every change of {@link tables} or the local stores. */
export const dbVersion = 2

/** Members every synced row has that only the server sets (data model §1.1). */
export const syncedColumns = [
  'seq',
  'rev',
  'deleted',
  'deleteBatchId',
  'createdAt',
  'createdBy',
  'updatedAt',
  'updatedBy',
] as const

/** Rows below an item (by `itemId`): written with *edit* on the item, evicted with it. */
const belowItem = { table: 'items', key: 'itemId' } as const
const belowMap = { table: 'maps', key: 'mapId' } as const
const belowMilestone = { table: 'milestones', key: 'milestoneId' } as const
const belowTemplate = { table: 'project_templates', key: 'templateId' } as const

/** Read-only configuration (data model §7), written online through admin endpoints: synced, never pushed. */
function config(name: string, indexes: readonly string[] = [], parents: TableDef['parents'] = []): TableDef {
  return { name, indexes, parents, writes: 'none', since: 2 }
}

export const tables: readonly TableDef[] = [
  {
    name: 'items',
    // `originItemId`: the projects converted from an idea or lead (the source shows *Converted* before the server's
    // `convertedToItemId` arrives).
    indexes: ['type', 'statusId', 'ownerUserId', 'originItemId'],
    parents: [],
    writes: 'all',
    root: 'item',
    // `access` is computed per user (data model §1.2); the server sets the origin's `convertedToItemId` when it accepts
    // a project with `originItemId` (docs/03 §12.8).
    serverOwned: ['number', 'statusChangedAt', 'convertedToItemId', 'access'],
    defaults: ({ userId }) => ({
      number: null,
      description: null,
      color: null,
      imageFileId: null,
      ownerUserId: userId,
      statusId: null,
      statusChangedAt: null,
      tags: [],
      originItemId: null,
      convertedToItemId: null,
      pinned: false,
      access: 'manage',
    }),
    since: 1,
    revised: 2,
    before: ['type', 'statusId', 'ownerUserId'],
  },
  {
    name: 'item_shares',
    indexes: ['itemId', 'groupId'],
    parents: [belowItem],
    writes: 'insert',
    since: 1,
  },
  { name: 'groups', indexes: [], parents: [], writes: 'none', since: 1 },
  { name: 'persons', indexes: ['userId', 'email'], parents: [], writes: 'all', since: 1 },
  {
    name: 'user_settings',
    indexes: ['key'],
    parents: [],
    writes: 'all',
    defaults: ({ userId }) => ({ userId }),
    since: 1,
  },
  // --- phase 3: every other synced table of the data model, so that phases 4 and 5 need no version of their own ---
  config('statuses', ['itemType']),
  {
    name: 'assignments',
    indexes: ['itemId', 'milestoneId', 'personId'],
    parents: [belowItem, belowMilestone],
    writes: 'all',
    defaults: () => ({ itemId: null, milestoneId: null, role: null }),
    since: 2,
  },
  {
    name: 'item_links',
    indexes: ['fromItemId', 'toItemId'],
    // A link belongs to its source; the target is resolved locally (restricted when it isn't on the device).
    parents: [{ table: 'items', key: 'fromItemId' }],
    writes: 'all',
    since: 2,
  },
  {
    name: 'files',
    indexes: ['itemId', 'mapId'],
    parents: [belowItem, belowMap],
    writes: 'all',
    // `status` becomes `available` when the server has the bytes (the commit, docs/03 §10.2).
    serverOwned: ['status'],
    defaults: () => ({ itemId: null, mapId: null, width: null, height: null, thumbnail: null, status: 'pending' }),
    blobs: true,
    since: 2,
  },
  config('sections'),
  config('field_definitions', ['sectionId'], [{ table: 'sections', key: 'sectionId' }]),
  { name: 'field_values', indexes: ['itemId', 'fieldId'], parents: [belowItem], writes: 'all', since: 2 },
  { name: 'item_sections', indexes: ['itemId', 'sectionId'], parents: [belowItem], writes: 'all', since: 2 },
  config('project_templates'),
  config('template_statuses', ['templateId'], [belowTemplate]),
  config('template_sections', ['templateId'], [belowTemplate]),
  config('template_field_defaults', ['templateId'], [belowTemplate]),
  config('template_milestones', ['templateId'], [belowTemplate]),
  {
    name: 'saved_views',
    indexes: ['itemType'],
    parents: [],
    writes: 'all',
    defaults: ({ userId }) => ({ ownerUserId: userId, isShared: false, sortOrder: 0 }),
    since: 2,
  },
  config('assignment_roles'),
  { name: 'milestones', indexes: ['itemId'], parents: [belowItem], writes: 'all', since: 2 },
  { name: 'dod_items', indexes: ['milestoneId'], parents: [belowMilestone], writes: 'all', since: 2 },
  {
    name: 'maps',
    indexes: ['ownerUserId'],
    parents: [],
    writes: 'all',
    root: 'map',
    serverOwned: ['access'],
    defaults: ({ userId }) => ({ ownerUserId: userId, description: null, isTemplate: false, access: 'manage' }),
    since: 2,
  },
  { name: 'map_shares', indexes: ['mapId', 'groupId'], parents: [belowMap], writes: 'insert', since: 2 },
  { name: 'map_layers', indexes: ['mapId'], parents: [belowMap], writes: 'all', since: 2 },
  { name: 'map_elements', indexes: ['mapId', 'layerId', 'itemId'], parents: [belowMap], writes: 'all', since: 2 },
]

const byName = new Map(tables.map((t) => [t.name, t]))

/** A table's entry; undefined for a table this version doesn't know (a newer server: its rows are ignored). */
export function tableDef(name: string): TableDef | undefined {
  return byName.get(name)
}

/** The tables whose rows hang below rows of `parent`, with the member that names the parent. */
export function childrenOf(parent: string): { table: string; key: string }[] {
  return tables.flatMap((t) => t.parents.filter((p) => p.table === parent).map((p) => ({ table: t.name, key: p.key })))
}

/** The table holding the roots of an eviction kind (`item` → `items`). */
export function rootTable(kind: string): TableDef | undefined {
  return tables.find((t) => t.root === kind)
}

/** The stores the local database has besides the synced tables (data model §14). */
export const localStores = {
  outbox: '++seq, [table+id], batch, mutationId',
  conflicts: 'id, [table+rowId], mapId',
  blobs: 'id, cachedAt',
  drafts: 'key',
  meta: 'key',
} as const

/** A table's Dexie schema string: the key, then the indexes. */
export function storeSpec(table: TableDef, version = dbVersion): string {
  const old = table.revised !== undefined && version < table.revised
  return ['id', ...(old ? (table.before ?? table.indexes) : table.indexes)].join(', ')
}

/**
 * The Dexie schema per version, from the registry: version 1 has the local stores and every table of version 1; each
 * later version declares the tables it adds or revises (Dexie keeps the others). Additive only (docs/03 §11.3).
 */
export function schemaVersions(): { version: number; stores: Record<string, string> }[] {
  const versions: { version: number; stores: Record<string, string> }[] = []
  for (let version = 1; version <= dbVersion; version++) {
    const stores: Record<string, string> = version === 1 ? { ...localStores } : {}
    for (const table of tables) {
      if (table.since === version || table.revised === version) stores[table.name] = storeSpec(table, version)
    }
    versions.push({ version, stores })
  }
  return versions
}
