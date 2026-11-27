import { computed, shallowRef, type ShallowRef } from 'vue'
import { applyPatch, type Changes, type Row } from '@/local/outbox'
import type { MapDataSource, MapTable } from './dataSource'
import { uuidv7 } from './ids'
import type {
  Access,
  AssignmentRow,
  DodItemRow,
  ElementRow,
  FieldDefinitionRow,
  FieldValueRow,
  ItemRow,
  LayerRow,
  LinkRow,
  MapRow,
  MapViewSettings,
  MilestoneRow,
  PersonRow,
  StatusRow,
} from './types'

/**
 * A map's data in memory (dataSource.ts): the tests' and the playground's data source, with the repository's API.
 * Writes apply at once (a batch publishes its rows when it ends) and are logged; a user with read access is refused, as
 * the server would. The read-only tables (items, field values, …) change through `put` and `drop`, as the sync would.
 */

export interface MemorySeed {
  map: MapRow
  layers?: LayerRow[]
  elements?: ElementRow[]
  items?: ItemRow[]
  statuses?: StatusRow[]
  milestones?: MilestoneRow[]
  dodItems?: DodItemRow[]
  links?: LinkRow[]
  assignments?: AssignmentRow[]
  persons?: PersonRow[]
  fieldDefinitions?: FieldDefinitionRow[]
  fieldValues?: FieldValueRow[]
  /** Default: the map row's `access`, else edit. */
  access?: Access
  viewSettings?: MapViewSettings | null
  /** File id → URL. */
  files?: Record<string, string>
}

/** One write, as the outbox would get it. */
export interface Mutation {
  batch: string | null
  table: MapTable
  op: 'insert' | 'patch' | 'delete'
  id: string
  changes?: Changes
  row?: Row
}

type ReadTables = {
  items: ItemRow
  statuses: StatusRow
  milestones: MilestoneRow
  dod_items: DodItemRow
  item_links: LinkRow
  assignments: AssignmentRow
  persons: PersonRow
  field_definitions: FieldDefinitionRow
  field_values: FieldValueRow
}
export type ReadTable = keyof ReadTables

export interface MemorySource extends MapDataSource {
  readonly log: Readonly<ShallowRef<readonly Mutation[]>>
  setAccess(access: Access): void
  /** Adds or replaces a row of a table the map only reads. */
  put<T extends ReadTable>(table: T, row: ReadTables[T]): void
  drop(table: ReadTable, id: string): void
  setFile(id: string, url: string): void
}

/** A table: rows by id (in insertion order), published as an array when it changed. */
class Table<T extends { id: string }> {
  readonly rows = new Map<string, T>()
  readonly published: ShallowRef<readonly T[]>

  constructor(rows: readonly T[] = []) {
    for (const row of rows) this.rows.set(row.id, row)
    this.published = shallowRef([...this.rows.values()])
  }

  publish() {
    this.published.value = [...this.rows.values()]
  }
}

export function createMemorySource(seed: MemorySeed): MemorySource {
  const mapRow = shallowRef<MapRow | null>(seed.map)
  const write: Record<'map_layers' | 'map_elements', Table<Row & { id: string }>> = {
    map_layers: new Table(seed.layers as (Row & { id: string })[]),
    map_elements: new Table(seed.elements as (Row & { id: string })[]),
  }
  const read: { [T in ReadTable]: Table<ReadTables[T]> } = {
    items: new Table(seed.items),
    statuses: new Table(seed.statuses),
    milestones: new Table(seed.milestones),
    dod_items: new Table(seed.dodItems),
    item_links: new Table(seed.links),
    assignments: new Table(seed.assignments),
    persons: new Table(seed.persons),
    field_definitions: new Table(seed.fieldDefinitions),
    field_values: new Table(seed.fieldValues),
  }
  const access = shallowRef<Access>(seed.access ?? seed.map.access ?? 'edit')
  const viewSettings = shallowRef<MapViewSettings | null>(seed.viewSettings ?? null)
  const files = new Map(Object.entries(seed.files ?? {}))
  const log = shallowRef<readonly Mutation[]>([])

  let depth = 0
  let batchId: string | null = null
  const dirty = new Set<{ publish(): void }>()
  let pending: Mutation[] = []

  function flush() {
    if (depth > 0) return
    for (const table of dirty) table.publish()
    dirty.clear()
    if (pending.length) {
      log.value = [...log.value, ...pending]
      pending = []
    }
  }

  function refuse(): Promise<never> | null {
    return access.value === 'read' ? Promise.reject(new Error('Read access only: the map cannot be changed')) : null
  }

  function record(mutation: Omit<Mutation, 'batch'>) {
    pending.push({ ...mutation, batch: batchId })
  }

  function create(table: MapTable, row: Row): Promise<void> {
    const refused = refuse()
    if (refused) return refused
    const id = row.id
    if (typeof id !== 'string') return Promise.reject(new Error('A row needs an id'))
    if (table === 'maps') return Promise.reject(new Error('Maps are created by the host'))
    const target = write[table]
    if (target.rows.has(id)) return Promise.reject(new Error(`${table} ${id} exists`))
    target.rows.set(id, row as Row & { id: string })
    dirty.add(target)
    record({ table, op: 'insert', id, row })
    flush()
    return Promise.resolve()
  }

  function patch(table: MapTable, id: string, changes: Changes): Promise<void> {
    const refused = refuse()
    if (refused) return refused
    if (table === 'maps') {
      if (!mapRow.value || mapRow.value.id !== id) return Promise.reject(new Error(`No map ${id}`))
      mapRow.value = applyPatch(mapRow.value, changes)
    } else {
      const target = write[table]
      const row = target.rows.get(id)
      if (!row) return Promise.reject(new Error(`No ${table} ${id}`))
      target.rows.set(id, applyPatch(row, changes))
      dirty.add(target)
    }
    record({ table, op: 'patch', id, changes })
    flush()
    return Promise.resolve()
  }

  function remove(table: MapTable, id: string): Promise<void> {
    const refused = refuse()
    if (refused) return refused
    if (table === 'maps') return Promise.reject(new Error('Maps are deleted by the host'))
    const target = write[table]
    if (!target.rows.delete(id)) return Promise.reject(new Error(`No ${table} ${id}`))
    dirty.add(target)
    record({ table, op: 'delete', id })
    flush()
    return Promise.resolve()
  }

  function end() {
    depth--
    if (depth === 0) {
      batchId = null
      flush()
    }
  }

  function batch<T>(fn: () => T | Promise<T>): Promise<T> {
    if (depth === 0) batchId = uuidv7()
    depth++
    let result: T | Promise<T>
    try {
      result = fn()
    } catch (error) {
      end()
      return Promise.reject(error)
    }
    if (result instanceof Promise) return result.finally(end)
    end()
    return Promise.resolve(result)
  }

  return {
    map: mapRow,
    layers: write.map_layers.published as unknown as ShallowRef<readonly LayerRow[]>,
    elements: write.map_elements.published as unknown as ShallowRef<readonly ElementRow[]>,
    items: read.items.published,
    statuses: read.statuses.published,
    milestones: read.milestones.published,
    dodItems: read.dod_items.published,
    links: read.item_links.published,
    assignments: read.assignments.published,
    persons: read.persons.published,
    fieldDefinitions: read.field_definitions.published,
    fieldValues: read.field_values.published,
    access: computed(() => access.value),
    viewSettings,
    log,
    async setViewSettings(settings) {
      viewSettings.value = settings
    },
    async fileUrl(fileId) {
      return files.get(fileId) ?? null
    },
    newId: () => uuidv7(),
    create,
    patch,
    remove,
    batch,
    setAccess(next) {
      access.value = next
    },
    put(table, row) {
      const target = read[table] as unknown as Table<{ id: string }>
      target.rows.set(row.id, row)
      target.publish()
    },
    drop(table, id) {
      const target = read[table] as unknown as Table<{ id: string }>
      if (target.rows.delete(id)) target.publish()
    },
    setFile(id, url) {
      files.set(id, url)
    },
  }
}
