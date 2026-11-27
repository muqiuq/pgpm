import type { ShareSpec } from '@/api/sync'
import { compareKeyThenId, keyBetween } from '@/domain/fractional'
import { boundsOf } from '@/maps/geometry'
import { fromMapRow, toMapRow } from '@/maps/mapRows'
import { instantiateTemplate, mapFromTemplate } from '@/maps/templates'
import type { ElementRow, LayerRow, MapRow, MapSettings, MatrixSettings, MapViewSettings } from '@/maps/types'
import type { PgpmDb, StoredRow } from './db'
import { inTransaction, type RepoTransaction } from './repo'
import type { Access, FileRow, Stored, SyncedColumns } from './rows'
import { syncedColumns } from './tables'
import { uuidv7 } from './uuid'

/**
 * Maps on the device (doc 01 §12.1, §12.9, docs/03 §13.6): what the map list, the map page and an item's *Maps* tab
 * write – a new map (canvas with its layer *Base*, matrix from a preset or with its own axes, or from a template) with
 * its shares in one batch, a duplicate, a card of an item at a map's centre – all offline, through repository
 * transactions. The rows are stored camelCase (as synced); the map UI's snake_case helpers (templates.ts) are applied
 * through mapRows.ts.
 */

export type MapKind = 'canvas' | 'matrix'

/** `maps` (data model §9) as stored. */
export type MapRecord = SyncedColumns & {
  kind: MapKind
  name: string
  description: string | null
  ownerUserId: string
  settings: MapSettings | null
  matrix: MatrixSettings | null
  defaultView: MapViewSettings | null
  isTemplate: boolean
  templateSourceId: string | null
  /** A small WebP (or PNG) thumbnail, base64. */
  preview: string | null
  access: Access
}

/** The settings of a new canvas: grid on, no snapping. */
export const DEFAULT_SETTINGS: MapSettings = { grid: true, snap: false, gridSize: 20 }

/** The Base layer every map starts with (data model §9). */
export function baseLayer(mapId: string): LayerRow {
  return { id: uuidv7(), map_id: mapId, name: 'Base', sort_order: 'a0', is_visible: true, is_locked: false }
}

export function mapNameError(name: string): string | null {
  const n = name.trim()
  if (!n) return 'A name is needed.'
  return n.length > 200 ? 'At most 200 characters.' : null
}

const alive = <T extends StoredRow>(rows: readonly (T | undefined)[]): T[] =>
  rows.filter((r): r is T => !!r && r.deleted !== true)

/** A map with what a copy takes along: its layers, elements and the files of its images. */
export interface MapContent {
  map: MapRow
  layers: LayerRow[]
  elements: ElementRow[]
  files: Stored<FileRow>[]
}

export function mapContent(db: PgpmDb, mapId: string): Promise<MapContent | null> {
  // Dexie promises only, awaited directly: callable inside a repository transaction (docs/03 §12.8 *Dexie zones*).
  const read = async (): Promise<MapContent | null> => {
    const map = await db.synced('maps').get(mapId)
    if (!map || map.deleted === true) return null
    const layers = alive(await db.synced('map_layers').where('mapId').equals(mapId).toArray())
    const elements = alive(await db.synced('map_elements').where('mapId').equals(mapId).toArray())
    const files = alive(await db.synced('files').where('mapId').equals(mapId).toArray())
    return {
      map: toMapRow<MapRow>(map),
      layers: layers.map((l) => toMapRow<LayerRow>(l)),
      elements: elements.map((e) => toMapRow<ElementRow>(e)),
      files: files as unknown as Stored<FileRow>[],
    }
  }
  return inTransaction(read())
}

/** Rows referenced by others go last: a line after the elements it connects. */
function referencesLast(elements: readonly ElementRow[]): ElementRow[] {
  return [...elements].sort(
    (a, b) => Number(!!(a.start_element_id || a.end_element_id)) - Number(!!(b.start_element_id || b.end_element_id)),
  )
}

/**
 * Copies of the files of a copied map's images (a map image belongs to its map, docs/03 §10.3): the same bytes – the
 * server takes a known SHA-256 at once, no upload – and their local bytes, if any. An image whose file isn't on the
 * device is left out.
 */
function copyImages(
  tx: RepoTransaction,
  elements: ElementRow[],
  files: readonly Stored<FileRow>[],
  mapId: string,
): Promise<ElementRow[]> {
  return inTransaction(copyImagesIn(tx, elements, files, mapId))
}

async function copyImagesIn(
  tx: RepoTransaction,
  elements: ElementRow[],
  files: readonly Stored<FileRow>[],
  mapId: string,
): Promise<ElementRow[]> {
  const byId = new Map(files.map((f) => [f.id, f]))
  const copies = new Map<string, string>()
  const out: ElementRow[] = []
  for (const e of elements) {
    if (e.kind !== 'image') {
      out.push(e)
      continue
    }
    const file = e.file_id ? byId.get(e.file_id) : undefined
    if (!file) continue
    let copy = copies.get(file.id)
    if (!copy) {
      copy = uuidv7()
      copies.set(file.id, copy)
      await tx.create('files', {
        id: copy,
        itemId: null,
        mapId,
        purpose: 'map_image',
        fileName: file.fileName,
        contentType: file.contentType,
        sizeBytes: file.sizeBytes,
        sha256: file.sha256,
        width: file.width,
        height: file.height,
        thumbnail: file.thumbnail,
      })
      const bytes = await tx.db.blobs.get(file.id)
      if (bytes) {
        const uploaded = file.status === 'available'
        await tx.db.blobs.put({
          ...bytes,
          id: copy,
          cachedAt: new Date().toISOString(),
          origin: uploaded ? 'download' : 'local',
          uploaded,
          nextChunk: undefined,
          error: null,
        })
      }
    }
    out.push({ ...e, file_id: copy })
  }
  return out
}

export interface NewMap {
  kind: MapKind
  name: string
  description?: string | null
  shares: readonly ShareSpec[]
  settings?: MapSettings
  /** Matrix maps: the axes, quadrants and filter (a preset's, or chosen). */
  matrix?: MatrixSettings | null
  /** A template (or a matrix preset) to start from: its layers and drawings are copied (templates.ts). */
  template?: MapContent | null
}

/** Inside a transaction: the map, its layers (and a template's drawings), its shares – one batch, offline too. */
export function createMap(tx: RepoTransaction, input: NewMap): Promise<Stored<MapRecord>> {
  const work = async () => {
    const id = uuidv7()
    let row: Record<string, unknown>
    let layers: LayerRow[]
    let elements: ElementRow[] = []
    if (input.template) {
      const t = input.template
      row = fromMapRow(mapFromTemplate(t.map, { id, name: input.name.trim() }))
      if (input.description !== undefined) row.description = input.description
      if (input.matrix) row.matrix = input.matrix
      const copy = instantiateTemplate(t.map, t.elements, t.layers, { mapId: id, newId: uuidv7 })
      layers = copy.layers
      elements = await copyImages(tx, copy.elements, t.files, id)
    } else {
      row = {
        kind: input.kind,
        name: input.name.trim(),
        description: input.description ?? null,
        settings: input.settings ?? { ...DEFAULT_SETTINGS },
        matrix: input.kind === 'matrix' ? (input.matrix ?? null) : null,
        defaultView: null,
        isTemplate: false,
        templateSourceId: null,
      }
      layers = [baseLayer(id)]
    }
    delete row.ownerUserId
    const map = await tx.create<MapRecord>('maps', row as Partial<MapRecord>, { id })
    for (const layer of layers) await tx.create('map_layers', fromMapRow(layer), { id: layer.id })
    for (const element of referencesLast(elements))
      await tx.create('map_elements', fromMapRow(element), { id: element.id })
    for (const share of input.shares) {
      await tx.create('map_shares', { mapId: id, groupId: share.groupId, access: share.access })
    }
    return map as Stored<MapRecord>
  }
  return inTransaction(work())
}

/**
 * Inside a transaction: a copy of a map – its settings, layers and elements with new ids, connections kept – as a new
 * map of the user's (not a template). Cards, milestones and timelines whose item isn't on the device (the server
 * would refuse them) are left out; lines to them become free ends.
 */
export function duplicateMap(
  tx: RepoTransaction,
  content: MapContent,
  input: { name: string; shares: readonly ShareSpec[] },
): Promise<Stored<MapRecord>> {
  const work = async () => {
    const id = uuidv7()
    const source = content.map
    const items = new Set(
      alive(
        await tx.db
          .synced('items')
          .bulkGet([
            ...new Set(content.elements.flatMap((e) => [e.item_id, e.project_id]).filter((x) => !!x)),
          ] as string[]),
      ).map((i) => i.id),
    )
    const milestones = new Set(
      alive(
        await tx.db
          .synced('milestones')
          .bulkGet(content.elements.map((e) => e.milestone_id).filter((x): x is string => !!x)),
      ).map((m) => m.id),
    )
    const layerIds = new Map(content.layers.map((l) => [l.id, uuidv7()]))
    const kept = content.elements.filter(
      (e) =>
        layerIds.has(e.layer_id) &&
        (e.kind !== 'item' || (!!e.item_id && items.has(e.item_id))) &&
        (e.kind !== 'timeline' || (!!e.project_id && items.has(e.project_id))) &&
        (e.kind !== 'milestone' || (!!e.milestone_id && milestones.has(e.milestone_id))),
    )
    const elementIds = new Map(kept.map((e) => [e.id, uuidv7()]))
    const rest = fromMapRow({ ...source, id, name: input.name.trim(), is_template: false, template_source_id: null })
    for (const key of [...syncedColumns, 'access', 'ownerUserId']) delete rest[key]
    const map = await tx.create<MapRecord>('maps', rest as Partial<MapRecord>, { id })
    for (const l of content.layers) {
      await tx.create(
        'map_layers',
        { mapId: id, name: l.name, sortOrder: l.sort_order, isVisible: l.is_visible, isLocked: l.is_locked },
        { id: layerIds.get(l.id)! },
      )
    }
    const copied: ElementRow[] = kept.map((e) => ({
      id: elementIds.get(e.id)!,
      map_id: id,
      layer_id: layerIds.get(e.layer_id)!,
      kind: e.kind,
      z: e.z,
      x: e.x,
      y: e.y,
      width: e.width,
      height: e.height,
      item_id: e.item_id ?? null,
      milestone_id: e.milestone_id ?? null,
      project_id: e.project_id ?? null,
      file_id: e.file_id ?? null,
      text: e.text ?? null,
      points: e.points ?? null,
      start_element_id: e.start_element_id ? (elementIds.get(e.start_element_id) ?? null) : null,
      end_element_id: e.end_element_id ? (elementIds.get(e.end_element_id) ?? null) : null,
      style: e.style ?? {},
      config: e.config ?? null,
    }))
    for (const e of referencesLast(await copyImages(tx, copied, content.files, id))) {
      await tx.create('map_elements', fromMapRow(e), { id: e.id })
    }
    for (const share of input.shares) {
      await tx.create('map_shares', { mapId: id, groupId: share.groupId, access: share.access })
    }
    return map as Stored<MapRecord>
  }
  return inTransaction(work())
}

/** The layer new cards go on from outside the map (an item's *Add to map…*): the topmost visible, unlocked one. */
export function targetLayer(layers: readonly LayerRow[]): LayerRow | null {
  const sorted = [...layers].sort((a, b) => compareKeyThenId(a.sort_order, a.id, b.sort_order, b.id))
  for (let i = sorted.length - 1; i >= 0; i--) if (sorted[i]!.is_visible && !sorted[i]!.is_locked) return sorted[i]!
  return null
}

/** Where a card goes on a map seen from outside: the middle of its elements (the origin of an empty map). */
export function mapCentre(elements: readonly Pick<ElementRow, 'x' | 'y' | 'width' | 'height'>[]): {
  x: number
  y: number
} {
  const box = boundsOf(elements, { minX: 0, minY: 0, maxX: 0, maxY: 0 })
  return { x: (box.minX + box.maxX) / 2, y: (box.minY + box.maxY) / 2 }
}

export const CARD_SIZE = { width: 220, height: 92 }

/**
 * Inside a transaction: a card of the item at the map's centre, on top of its target layer (docs/01 §12.4 *Add to
 * map…*). Answers the new element's id; throws for a matrix map (its cards are computed) or one without a free layer.
 */
export function addCardToMap(tx: RepoTransaction, mapId: string, itemId: string): Promise<string> {
  return inTransaction(placeCards(tx, mapId, [itemId]).then((ids) => ids[0]!))
}

/** The gap between the cards *Add to map* lays out in a grid. */
export const CARD_GAP = 24

/** Where `count` cards go around a centre: a grid about as wide as high, row by row (top left of each card). */
export function cardGrid(centre: { x: number; y: number }, count: number): { x: number; y: number }[] {
  const columns = Math.max(1, Math.ceil(Math.sqrt(count)))
  const rows = Math.ceil(count / columns)
  const width = columns * CARD_SIZE.width + (columns - 1) * CARD_GAP
  const height = rows * CARD_SIZE.height + (rows - 1) * CARD_GAP
  const round = (v: number) => Math.round(v * 100) / 100
  return Array.from({ length: count }, (_, i) => ({
    x: round(centre.x - width / 2 + (i % columns) * (CARD_SIZE.width + CARD_GAP)),
    y: round(centre.y - height / 2 + Math.floor(i / columns) * (CARD_SIZE.height + CARD_GAP)),
  }))
}

/**
 * Inside a transaction: a card of each item (the list's bulk *Add to map…*, docs/01 §4.3) in a grid around the map's
 * centre, in the given order, on top of the target layer – one batch with the caller's. Answers the new elements' ids;
 * throws as {@link addCardToMap} does.
 */
export function addCardsToMap(tx: RepoTransaction, mapId: string, itemIds: readonly string[]): Promise<string[]> {
  return inTransaction(placeCards(tx, mapId, itemIds))
}

async function placeCards(tx: RepoTransaction, mapId: string, itemIds: readonly string[]): Promise<string[]> {
  const content = await mapContent(tx.db, mapId)
  if (!content) throw new Error('The map is not on this device.')
  if (content.map.kind === 'matrix') throw new Error('A matrix map places its items itself.')
  if (content.map.access === 'read') throw new Error('You can read this map, not change it.')
  const layer = targetLayer(content.layers)
  if (!layer) throw new Error('Every layer of the map is hidden or locked.')
  const spots = cardGrid(mapCentre(content.elements), itemIds.length)
  let z =
    content.elements
      .filter((e) => e.layer_id === layer.id)
      .sort((a, b) => compareKeyThenId(a.z, a.id, b.z, b.id))
      .at(-1)?.z ?? null
  const ids: string[] = []
  for (const [i, itemId] of itemIds.entries()) {
    const id = uuidv7()
    z = keyBetween(z, null)
    await tx.create(
      'map_elements',
      {
        mapId,
        layerId: layer.id,
        kind: 'item',
        itemId,
        z,
        x: spots[i]!.x,
        y: spots[i]!.y,
        width: CARD_SIZE.width,
        height: CARD_SIZE.height,
        milestoneId: null,
        projectId: null,
        fileId: null,
        text: null,
        points: null,
        startElementId: null,
        endElementId: null,
        style: {},
        config: { size: 'compact' },
      },
      { id },
    )
    ids.push(id)
  }
  return ids
}

/** How often an item is placed on each map (its cards), from the device's elements. */
export async function placementsOf(db: PgpmDb, itemId: string): Promise<Map<string, number>> {
  const counts = new Map<string, number>()
  for (const e of alive(await db.synced('map_elements').where('itemId').equals(itemId).toArray())) {
    if (e.kind !== 'item') continue
    counts.set(e.mapId as string, (counts.get(e.mapId as string) ?? 0) + 1)
  }
  return counts
}
