import Dexie, { liveQuery, type Subscription } from 'dexie'
import { computed, effectScope, getCurrentScope, onScopeDispose, ref, shallowRef, watch, type Ref } from 'vue'
import { localDb, type PgpmDb, type StoredRow } from '@/local/db'
import type { ObjectUrls } from '@/local/files'
import { fromBase64 } from '@/local/images'
import { jsonEqual, type Changes, type Row } from '@/local/outbox'
import { LocalWriteError, repo as appRepo, type Repo, type RepoTransaction } from '@/local/repo'
import { uuidv7 } from '@/local/uuid'
import type { MapDataSource, MapTable } from './dataSource'
import { fromMapRow, Reconciler, repoPatch, sameStored, toMapRow } from './mapRows'
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
  MatrixSettings,
  MilestoneRow,
  PersonRow,
  StatusRow,
} from './types'

/**
 * A map's MapDataSource over the local database (docs/03 §13.4, §12.5): reads are Dexie live queries – in this tab and
 * every other – whose rows are reconciled by id, `seq` and `rev` (mapRows.ts), so an unchanged row keeps its object and
 * only what changed re-renders; writes go through the repository, so each updates the row and appends its outbox entry
 * in one transaction, and the sync spreads it (live ~0.2 s later).
 *
 * - **Reads**: the map row, its layers and elements; the items its elements refer to (cards, timelines, a placed
 *   milestone's project) and, on a matrix map, the candidates of `maps.matrix.filter` (types, statuses, templates, and
 *   the shares for `groupIds`); the milestones of the projects among them and the placed ones, their DoD items; links
 *   with both ends among the items; the assignments of the items and milestones and the persons they name; the items'
 *   field values; all statuses and field definitions; the map's computed `access`; `user_settings` `map:<id>`.
 * - **Writes**: `create`/`patch`/`remove` on the map's layers and elements (and `patch` on the map), refused with read
 *   access. A patch's `{old, new}` go to the repository as they are: `old` is what the user saw, which decides a
 *   conflict on the server. `batch(fn)` calls `fn` at once; every write made until it ends (its promise settles) waits
 *   and goes into **one** repository transaction – one outbox batch, in the order made (rows before the patches that
 *   refer to them, as the editor orders them). A batch's writes settle when it is stored: `fn` mustn't await them.
 * - **Files**: `fileUrl` answers `blob:` URLs of the bytes on this device (the row's thumbnail when asked), downloads
 *   through the file pipeline when they aren't (online), and else waits until they arrive; the source revokes its URLs
 *   on `dispose()`.
 * - **View settings**: published at once, written (one row per user and map) in order.
 */

export interface LocalSourceOptions {
  db?: () => PgpmDb
  repo?: Repo
  /** Downloads a file's bytes that aren't on the device (the app's file pipeline); none: local bytes only. */
  files?: { fileBlob(id: string): Promise<Blob> } | null
  objectUrls?: ObjectUrls
}

export interface LocalMapSource extends MapDataSource {
  readonly mapId: string
  /** The map, its layers and elements have been read once (null `map` then: not on this device). */
  readonly loaded: Readonly<Ref<boolean>>
  /** The last failure of a read. */
  readonly error: Readonly<Ref<unknown>>
  /** How many write operations (a batch counts once) this source made: the host renders a preview only after some. */
  readonly writes: Readonly<Ref<number>>
  /** Ends the live queries and revokes the source's file URLs (also when the creating scope ends). */
  dispose(): void
}

/** A write the map refuses before it reaches the repository (read access, a row that exists). */
export class MapWriteError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'MapWriteError'
  }
}

const browserUrls: ObjectUrls = {
  create: (blob) => URL.createObjectURL(blob),
  revoke: (url) => URL.revokeObjectURL(url),
}

const liveRows = <T extends StoredRow>(rows: readonly (T | undefined)[]): T[] =>
  rows.filter((r): r is T => !!r && r.deleted !== true)

/** What a map's elements and its matrix refer to: the key of the related rows' query. */
interface References {
  items: string[]
  milestones: string[]
  filter: MatrixSettings['filter'] | null
}

interface Related {
  items: StoredRow[]
  milestones: StoredRow[]
  dodItems: StoredRow[]
  links: StoredRow[]
  assignments: StoredRow[]
  persons: StoredRow[]
  fieldValues: StoredRow[]
}

/** The items a matrix map shows (its filter; `groupIds`: shared with one of the groups). */
async function matrixCandidates(db: PgpmDb, filter: NonNullable<MatrixSettings['filter']> | Record<string, never>) {
  const f = filter as NonNullable<MatrixSettings['filter']>
  const types = f.types?.length ? f.types : null
  const items = db.synced('items')
  let rows = liveRows(types ? await items.where('type').anyOf(types).toArray() : await items.toArray())
  if (f.statusIds?.length) rows = rows.filter((r) => f.statusIds!.includes(r.statusId as string))
  if (f.templateIds?.length) rows = rows.filter((r) => f.templateIds!.includes(r.projectTemplateId as string))
  if (f.groupIds?.length) {
    const shares = liveRows(await db.synced('item_shares').where('groupId').anyOf(f.groupIds).toArray())
    const shared = new Set(shares.map((s) => s.itemId as string))
    rows = rows.filter((r) => shared.has(r.id))
  }
  return rows
}

async function readRelated(db: PgpmDb, refs: References): Promise<Related> {
  const placed = liveRows(await db.synced('milestones').bulkGet(refs.milestones))
  const ids = new Set(refs.items)
  for (const m of placed) ids.add(m.itemId as string)
  const items = new Map<string, StoredRow>()
  for (const item of liveRows(await db.synced('items').bulkGet([...ids]))) items.set(item.id, item)
  if (refs.filter) {
    for (const item of await Dexie.Promise.resolve(matrixCandidates(db, refs.filter))) items.set(item.id, item)
  }
  const itemIds = [...items.keys()]
  const projects = [...items.values()].filter((i) => i.type === 'project').map((i) => i.id)
  const milestones = new Map(placed.map((m) => [m.id, m]))
  if (projects.length) {
    for (const m of liveRows(await db.synced('milestones').where('itemId').anyOf(projects).toArray()))
      milestones.set(m.id, m)
  }
  const milestoneIds = [...milestones.keys()]
  const dodItems = milestoneIds.length
    ? liveRows(await db.synced('dod_items').where('milestoneId').anyOf(milestoneIds).toArray())
    : []
  const links = itemIds.length
    ? liveRows(await db.synced('item_links').where('fromItemId').anyOf(itemIds).toArray()).filter((l) =>
        items.has(l.toItemId as string),
      )
    : []
  const assignments = new Map<string, StoredRow>()
  if (itemIds.length) {
    for (const a of liveRows(await db.synced('assignments').where('itemId').anyOf(itemIds).toArray()))
      assignments.set(a.id, a)
  }
  if (milestoneIds.length) {
    for (const a of liveRows(await db.synced('assignments').where('milestoneId').anyOf(milestoneIds).toArray()))
      assignments.set(a.id, a)
  }
  const personIds = [...new Set([...assignments.values()].map((a) => a.personId as string))]
  const persons = personIds.length ? liveRows(await db.synced('persons').bulkGet(personIds)) : []
  const fieldValues = itemIds.length
    ? liveRows(await db.synced('field_values').where('itemId').anyOf(itemIds).toArray())
    : []
  return {
    items: [...items.values()],
    milestones: [...milestones.values()],
    dodItems,
    links,
    assignments: [...assignments.values()],
    persons,
    fieldValues,
  }
}

/** A write waiting for its batch to end. */
interface Queued {
  run: (tx: RepoTransaction) => Promise<void>
  resolve: () => void
  reject: (error: unknown) => void
}

export function createLocalSource(mapId: string, options: LocalSourceOptions = {}): LocalMapSource {
  const db = options.db ?? localDb
  const repo = options.repo ?? appRepo
  const objectUrls = options.objectUrls ?? browserUrls
  const scope = effectScope(true)
  const subscriptions = new Set<Subscription>()
  const error = shallowRef<unknown>(null)

  const map = shallowRef<MapRow | null>(null)
  let mapRaw: StoredRow | null = null
  const layers = shallowRef<readonly LayerRow[]>([])
  const elements = shallowRef<readonly ElementRow[]>([])
  const items = shallowRef<readonly ItemRow[]>([])
  const statuses = shallowRef<readonly StatusRow[]>([])
  const milestones = shallowRef<readonly MilestoneRow[]>([])
  const dodItems = shallowRef<readonly DodItemRow[]>([])
  const links = shallowRef<readonly LinkRow[]>([])
  const assignments = shallowRef<readonly AssignmentRow[]>([])
  const persons = shallowRef<readonly PersonRow[]>([])
  const fieldDefinitions = shallowRef<readonly FieldDefinitionRow[]>([])
  const fieldValues = shallowRef<readonly FieldValueRow[]>([])
  const viewSettings = shallowRef<MapViewSettings | null>(null)
  const writes = ref(0)
  const answered = { map: false, layers: false, elements: false }
  const loaded = ref(false)

  const reconcilers = {
    layers: new Reconciler<LayerRow>(toMapRow),
    elements: new Reconciler<ElementRow>(toMapRow),
    items: new Reconciler<ItemRow>(toMapRow),
    statuses: new Reconciler<StatusRow>(toMapRow),
    milestones: new Reconciler<MilestoneRow>(toMapRow),
    dodItems: new Reconciler<DodItemRow>(toMapRow),
    links: new Reconciler<LinkRow>(toMapRow),
    assignments: new Reconciler<AssignmentRow>(toMapRow),
    persons: new Reconciler<PersonRow>(toMapRow),
    fieldDefinitions: new Reconciler<FieldDefinitionRow>(toMapRow),
    fieldValues: new Reconciler<FieldValueRow>(toMapRow),
  }

  function follow<T>(query: () => Promise<T>, next: (value: T) => void): Subscription {
    const subscription = liveQuery(query).subscribe({
      next: (value) => {
        error.value = null
        next(value)
      },
      error: (e: unknown) => (error.value = e),
    })
    subscriptions.add(subscription)
    return subscription
  }

  function answer(part: keyof typeof answered) {
    answered[part] = true
    if (answered.map && answered.layers && answered.elements) loaded.value = true
  }

  const settingsKey = `map:${mapId}`
  scope.run(() => {
    follow(
      async () => (await db().synced('maps').get(mapId)) ?? null,
      (raw) => {
        answer('map')
        if (!raw || raw.deleted === true) {
          mapRaw = null
          map.value = null
        } else if (!mapRaw || !sameStored(mapRaw, raw)) {
          mapRaw = raw
          map.value = toMapRow<MapRow>(raw)
        }
      },
    )
    const ofMap = (table: string) => async () =>
      liveRows(await db().synced(table).where('mapId').equals(mapId).toArray())
    follow(ofMap('map_layers'), (rows) => {
      layers.value = reconcilers.layers.next(rows)
      answer('layers')
    })
    follow(ofMap('map_elements'), (rows) => {
      elements.value = reconcilers.elements.next(rows)
      answer('elements')
    })
    follow(
      async () => liveRows(await db().synced('statuses').toArray()),
      (rows) => (statuses.value = reconcilers.statuses.next(rows)),
    )
    follow(
      async () => liveRows(await db().synced('field_definitions').toArray()),
      (rows) => (fieldDefinitions.value = reconcilers.fieldDefinitions.next(rows)),
    )
    follow(
      async () =>
        (liveRows(await db().synced('user_settings').where('key').equals(settingsKey).toArray())[0]?.value ??
          null) as MapViewSettings | null,
      (value) => {
        if (!jsonEqual(value, viewSettings.value)) viewSettings.value = value
      },
    )

    // The related rows follow what the elements (and a matrix' filter) refer to: a new query when that changes.
    const references = computed(() => {
      const itemIds = new Set<string>()
      const milestoneIds = new Set<string>()
      for (const e of elements.value) {
        if (e.item_id) itemIds.add(e.item_id)
        if (e.project_id) itemIds.add(e.project_id)
        if (e.milestone_id) milestoneIds.add(e.milestone_id)
      }
      const m = map.value
      const refs: References = {
        items: [...itemIds].sort(),
        milestones: [...milestoneIds].sort(),
        filter: m?.kind === 'matrix' ? (m.matrix?.filter ?? {}) : null,
      }
      return JSON.stringify(refs)
    })
    let related: Subscription | null = null
    watch(
      references,
      (key) => {
        if (related) {
          related.unsubscribe()
          subscriptions.delete(related)
        }
        const refs = JSON.parse(key) as References
        related = follow(
          () => readRelated(db(), refs),
          (r) => {
            items.value = reconcilers.items.next(r.items)
            milestones.value = reconcilers.milestones.next(r.milestones)
            dodItems.value = reconcilers.dodItems.next(r.dodItems)
            links.value = reconcilers.links.next(r.links)
            assignments.value = reconcilers.assignments.next(r.assignments)
            persons.value = reconcilers.persons.next(r.persons)
            fieldValues.value = reconcilers.fieldValues.next(r.fieldValues)
          },
        )
      },
      { immediate: true },
    )
  })

  const access = computed<Access>(() => (map.value ? (map.value.access ?? 'manage') : 'read'))

  // --- writes -------------------------------------------------------------------------------------------------------

  let queue: Queued[] | null = null

  function write(run: Queued['run']): Promise<void> {
    if (access.value === 'read') {
      return Promise.reject(new MapWriteError('Read access only: the map cannot be changed.'))
    }
    if (queue) {
      const waiting = queue
      return new Promise<void>((resolve, reject) => waiting.push({ run, resolve, reject }))
    }
    writes.value++
    return repo.transaction(run)
  }

  /** The queued writes in one transaction: one that fails (a row gone) fails alone, the others are stored. */
  async function flush(queued: Queued[]) {
    if (!queued.length) return
    writes.value++
    const failed = new Map<Queued, unknown>()
    try {
      await repo.transaction(async (tx) => {
        for (const q of queued) {
          try {
            await q.run(tx)
          } catch (e) {
            if (e instanceof LocalWriteError || e instanceof MapWriteError) failed.set(q, e)
            else throw e
          }
        }
      })
    } catch (e) {
      for (const q of queued) q.reject(e)
      return
    }
    for (const q of queued) {
      if (failed.has(q)) q.reject(failed.get(q))
      else q.resolve()
    }
  }

  function create(table: MapTable, row: Row): Promise<void> {
    if (table === 'maps') return Promise.reject(new MapWriteError('Maps are created by the host.'))
    const id = row.id
    if (typeof id !== 'string') return Promise.reject(new MapWriteError('A row needs an id.'))
    const values = fromMapRow(row)
    return write((tx) =>
      tx.db
        .synced(table)
        .get(id)
        .then((existing) => {
          if (existing && existing.deleted !== true) throw new MapWriteError(`${table} ${id} exists.`)
          return tx.create(table, values, { id }).then(() => undefined)
        }),
    )
  }

  function patch(table: MapTable, id: string, changes: Changes): Promise<void> {
    const { values, old } = repoPatch(changes)
    return write((tx) => (Object.keys(values).length ? tx.patch(table, id, values, { old }) : Promise.resolve()))
  }

  function remove(table: MapTable, id: string): Promise<void> {
    if (table === 'maps') return Promise.reject(new MapWriteError('Maps are deleted by the host.'))
    return write((tx) =>
      tx.db
        .synced(table)
        .get(id)
        .then((existing) => {
          if (!existing || existing.deleted === true)
            throw new LocalWriteError('not_found', 'It was deleted meanwhile.')
          return tx.remove(table, id)
        }),
    )
  }

  function batch<T>(fn: () => T | Promise<T>): Promise<T> {
    // A batch inside a batch joins it.
    if (queue) {
      try {
        return Promise.resolve(fn())
      } catch (e) {
        return Promise.reject(e)
      }
    }
    const queued: Queued[] = []
    queue = queued
    const end = () => {
      if (queue === queued) queue = null
      return flush(queued)
    }
    let result: T | Promise<T>
    try {
      result = fn()
    } catch (e) {
      return end().then(() => Promise.reject(e))
    }
    if (result instanceof Promise) {
      return result.then(
        (value) => end().then(() => value),
        (e: unknown) => end().then(() => Promise.reject(e)),
      )
    }
    const value = result
    return end().then(() => value)
  }

  // --- view settings ------------------------------------------------------------------------------------------------

  let settingsWrites: Promise<void> = Promise.resolve()

  function setViewSettings(settings: MapViewSettings): Promise<void> {
    viewSettings.value = settings
    const value = JSON.parse(JSON.stringify(settings)) as MapViewSettings
    const next = settingsWrites.then(async () => {
      const row = liveRows(await db().synced('user_settings').where('key').equals(settingsKey).toArray())[0]
      if (!row) await repo.create('user_settings', { key: settingsKey, value })
      else if (!jsonEqual(row.value, value)) await repo.patch('user_settings', row.id, { value })
    })
    settingsWrites = next.catch(() => undefined)
    return next
  }

  // --- files --------------------------------------------------------------------------------------------------------

  const urls = new Map<string, string>()
  const waiting = new Set<() => void>()
  let disposed = false

  function remember(key: string, blob: Blob): string | null {
    if (disposed) return null
    const known = urls.get(key)
    if (known) return known
    const url = objectUrls.create(blob)
    urls.set(key, url)
    return url
  }

  /** The URL when the file is here now; undefined: not (yet) on this device. */
  async function urlNow(fileId: string, thumbnail: boolean, download: boolean): Promise<string | null | undefined> {
    const key = thumbnail ? `thumb:${fileId}` : fileId
    const known = urls.get(key) ?? urls.get(fileId)
    if (known) return known
    const d = db()
    const row = await d.synced('files').get(fileId)
    if (row?.deleted === true) return null
    if (thumbnail && typeof row?.thumbnail === 'string' && row.thumbnail) {
      try {
        return remember(key, new Blob([fromBase64(row.thumbnail)], { type: 'image/webp' }))
      } catch {
        // Not base64 after all: the image itself.
      }
    }
    const bytes = await d.blobs.get(fileId)
    if (bytes) return remember(fileId, new Blob([bytes.bytes], { type: bytes.type }))
    if (download && options.files && row) {
      try {
        return remember(fileId, await options.files.fileBlob(fileId))
      } catch {
        // Offline, not uploaded yet: wait for it.
      }
    }
    return undefined
  }

  /** Waits until the file's thumbnail or bytes are on the device (the sync, the prefetch); null once disposed. */
  function whenHere(fileId: string, thumbnail: boolean): Promise<string | null> {
    return new Promise((resolve) => {
      let subscription: Subscription | null = null
      const done = (url: string | null) => {
        waiting.delete(stop)
        subscription?.unsubscribe()
        resolve(url)
      }
      const stop = () => done(null)
      waiting.add(stop)
      subscription = liveQuery(async () => {
        const row = await db().synced('files').get(fileId)
        const here = await db().blobs.where('id').equals(fileId).count()
        return `${String(row?.deleted)}:${String(row?.thumbnail ? 1 : 0)}:${String(row?.status)}:${here}`
      }).subscribe({
        next: () => {
          void urlNow(fileId, thumbnail, true).then((url) => {
            if (url !== undefined && waiting.has(stop)) done(url)
          })
        },
        error: () => done(null),
      })
    })
  }

  async function fileUrl(fileId: string, opts: { thumbnail?: boolean } = {}): Promise<string | null> {
    if (disposed) return null
    const url = await urlNow(fileId, !!opts.thumbnail, true)
    if (disposed) return null
    return url === undefined ? whenHere(fileId, !!opts.thumbnail) : url
  }

  function dispose() {
    if (disposed) return
    disposed = true
    scope.stop()
    for (const s of subscriptions) s.unsubscribe()
    subscriptions.clear()
    for (const stop of [...waiting]) stop()
    for (const url of urls.values()) objectUrls.revoke(url)
    urls.clear()
  }
  if (getCurrentScope()) onScopeDispose(dispose)

  return {
    mapId,
    map,
    layers,
    elements,
    items,
    statuses,
    milestones,
    dodItems,
    links,
    assignments,
    persons,
    fieldDefinitions,
    fieldValues,
    access,
    viewSettings,
    loaded,
    error,
    writes,
    setViewSettings,
    fileUrl,
    newId: () => uuidv7(),
    create,
    patch,
    remove,
    batch,
    dispose,
  }
}
