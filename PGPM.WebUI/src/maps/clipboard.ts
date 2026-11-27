import { keysBetween } from '@/domain/fractional'
import type { Row } from '@/local/outbox'
import { boundsOf, type Point } from './geometry'
import { round2 } from './scene'
import type { ElementRow } from './types'

/**
 * Copy, paste and duplicate (doc 01 §12.2, docs/03 §13.4): elements travel as JSON (`{"pgpm":"elements", …}`) through
 * the system clipboard, within a map and across maps. Pasting creates new element ids; an item card stays a card of the
 * same item (another card), milestones and timelines keep their references. A line keeps its connections to elements
 * pasted with it; one to an element left behind becomes a free end where it was (the copy stores lines with their
 * drawn ends).
 */

export interface ClipboardData {
  pgpm: 'elements'
  version: 1
  /** The map the elements came from. */
  mapId: string
  /** In drawing order. */
  elements: ElementRow[]
}

/** Columns of synced rows (data model §1.1) a copy doesn't take along. */
export const SYNCED_COLUMNS = [
  'seq',
  'rev',
  'deleted',
  'delete_batch_id',
  'created_at',
  'created_by',
  'updated_at',
  'updated_by',
] as const

export function withoutSyncColumns<T extends Row>(row: T): T {
  const copy: Row = { ...row }
  for (const column of SYNCED_COLUMNS) delete copy[column]
  return copy as T
}

export function serializeElements(mapId: string, elements: readonly ElementRow[]): string {
  const data: ClipboardData = { pgpm: 'elements', version: 1, mapId, elements: elements.map(withoutSyncColumns) }
  return JSON.stringify(data)
}

const KINDS = new Set(['item', 'milestone', 'timeline', 'rect', 'ellipse', 'line', 'text', 'image'])

function isElement(value: unknown): value is ElementRow {
  if (typeof value !== 'object' || value === null) return false
  const e = value as Record<string, unknown>
  return (
    typeof e.id === 'string' &&
    typeof e.kind === 'string' &&
    KINDS.has(e.kind) &&
    [e.x, e.y, e.width, e.height].every((n) => typeof n === 'number' && Number.isFinite(n))
  )
}

/** The clipboard's text as map elements; null when it is anything else. */
export function parseClipboard(text: string | null | undefined): ClipboardData | null {
  if (!text || !text.trimStart().startsWith('{')) return null
  try {
    const data = JSON.parse(text) as Partial<ClipboardData>
    if (data?.pgpm !== 'elements' || !Array.isArray(data.elements)) return null
    const elements = data.elements.filter(isElement)
    return elements.length ? { pgpm: 'elements', version: 1, mapId: String(data.mapId ?? ''), elements } : null
  } catch {
    return null
  }
}

export interface PasteOptions {
  mapId: string
  /** The layer the copies go to. */
  layerId: string
  /** The layer's top key: the copies go above it, in their order. */
  topZ: string | null
  newId: () => string
  /** Moved by this much (default 20, 20: a duplicate next to its original)… */
  offset?: Point
  /** …or centred here (a paste into another place). */
  at?: Point | null
}

/** New rows for the pasted elements: new ids, the target layer and map, keys on top, connections among themselves. */
export function pasteElements(elements: readonly ElementRow[], options: PasteOptions): ElementRow[] {
  if (!elements.length) return []
  const ids = new Map(elements.map((e) => [e.id, options.newId()]))
  const keys = keysBetween(options.topZ, null, elements.length)
  let dx = options.offset?.x ?? 20
  let dy = options.offset?.y ?? 20
  if (options.at) {
    const box = boundsOf(elements, { minX: 0, minY: 0, maxX: 0, maxY: 0 })
    dx = options.at.x - (box.minX + box.maxX) / 2
    dy = options.at.y - (box.minY + box.maxY) / 2
  }
  return elements.map((e, i) => ({
    ...withoutSyncColumns(e),
    id: ids.get(e.id)!,
    map_id: options.mapId,
    layer_id: options.layerId,
    z: keys[i]!,
    x: round2(e.x + dx),
    y: round2(e.y + dy),
    start_element_id: e.start_element_id ? (ids.get(e.start_element_id) ?? null) : null,
    end_element_id: e.end_element_id ? (ids.get(e.end_element_id) ?? null) : null,
  }))
}
