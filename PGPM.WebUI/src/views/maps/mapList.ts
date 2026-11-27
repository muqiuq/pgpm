import { inputMax, scoreMax, type FieldConfig, type FieldType } from '@/domain/score'
import type { MapRecord } from '@/local/maps'
import type { ItemType } from '@/local/rows'
import { numericValue, type FieldDefinition } from '@/maps/cards'
import { matrixCandidates, matrixValues } from '@/maps/matrixItems'
import { DEFAULT_QUADRANT_LABELS, quadrantOf, type Quadrant } from '@/maps/matrixLayout'
import type { ItemRow as MapItemRow, MatrixSettings } from '@/maps/types'

/**
 * The map views' rules (doc 01 §12.1, §12.8), pure: which maps the tabs list, the matrix presets and canvas templates
 * *New map* offers, the fields a matrix axis can use with their ranges, the quadrant of each item for a matrix map
 * (the item lists' column and filter, the *Maps* tab).
 */

/** `field_definitions` as stored (camelCase), as far as the maps read them. */
export interface FieldDefinitionRecord {
  id: string
  key: string
  label: string
  type: FieldType
  config?: FieldConfig | null
  sectionId?: string | null
  isArchived?: boolean
  deleted?: boolean
}

/** What the library panel's rows carry when dragged onto the map: the item's id. */
export const ITEM_DRAG_TYPE = 'application/x-pgpm-item'

/** What *New map* makes (NewMapDialog.vue). */
export type NewMapMode = 'canvas' | 'matrix' | 'template' | 'duplicate'

export const KIND_LABELS: Record<MapRecord['kind'], string> = { canvas: 'Canvas', matrix: 'Matrix' }
export const KIND_ICONS: Record<MapRecord['kind'], string> = { canvas: 'pi pi-map', matrix: 'pi pi-th-large' }

/** The *Maps* tab's maps and the *Templates* tab's, by name. */
export function mapsOfTab(maps: readonly MapRecord[], tab: 'maps' | 'templates'): MapRecord[] {
  return maps
    .filter((m) => !m.deleted && (tab === 'templates') === !!m.isTemplate)
    .sort((a, b) => a.name.localeCompare(b.name))
}

/** Matrix templates (packages' presets) prefill *New matrix map*; the *Prioritization matrix* first. */
export function matrixPresets(maps: readonly MapRecord[]): MapRecord[] {
  return mapsOfTab(maps, 'templates')
    .filter((m) => m.kind === 'matrix' && !!m.matrix)
    .sort((a, b) => Number(b.name === 'Prioritization matrix') - Number(a.name === 'Prioritization matrix'))
}

export function canvasTemplates(maps: readonly MapRecord[]): MapRecord[] {
  return mapsOfTab(maps, 'templates').filter((m) => m.kind === 'canvas')
}

/** A field as a matrix axis can use it: numbers, ratings and scores, with the range the axis starts with. */
export interface AxisField {
  key: string
  label: string
  type: FieldType
  min: number
  max: number
}

export function axisFields(defs: readonly FieldDefinitionRecord[]): AxisField[] {
  return defs
    .filter((d) => !d.deleted && !d.isArchived && ['number', 'rating', 'score'].includes(d.type))
    .map((d) => {
      const max =
        d.type === 'score' ? (scoreMax(d) ?? 100) : d.type === 'rating' ? (inputMax(d) ?? 5) : (inputMax(d) ?? 100)
      const min = d.type === 'number' && typeof d.config?.min === 'number' ? d.config.min : 0
      return { key: d.key, label: d.label, type: d.type, min, max: max > min ? max : min + 1 }
    })
    .sort((a, b) => a.label.localeCompare(b.label))
}

/** A matrix with its own axes: each from its field's range, split in the middle, the default quadrant labels. */
export function customMatrix(x: AxisField, y: AxisField, types: ItemType[] = ['idea', 'project']): MatrixSettings {
  return {
    x: { field: x.key, label: x.label, min: x.min, max: x.max, split: (x.min + x.max) / 2 },
    y: { field: y.key, label: y.label, min: y.min, max: y.max, split: (y.min + y.max) / 2 },
    size: null,
    quadrants: { ...DEFAULT_QUADRANT_LABELS },
    filter: { types, statusIds: [], templateIds: [], groupIds: [] },
  }
}

/** A name for a copy: "Copy of X" (not over 200 characters). */
export function copyName(name: string): string {
  return `Copy of ${name}`.slice(0, 200)
}

/** What the quadrant column and filter need of a matrix map. */
export interface QuadrantMap {
  id: string
  name: string
  matrix: MatrixSettings
}

/** The matrix maps an item list can show a quadrant column for: those whose filter takes the type. */
export function quadrantMapsFor(maps: readonly MapRecord[], type: ItemType): QuadrantMap[] {
  return maps
    .filter((m) => !m.deleted && !m.isTemplate && m.kind === 'matrix' && !!m.matrix?.x && !!m.matrix?.y)
    .filter((m) => !m.matrix!.filter?.types?.length || m.matrix!.filter.types.includes(type))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((m) => ({ id: m.id, name: m.name, matrix: m.matrix! }))
}

export interface ItemQuadrant {
  quadrant: Quadrant
  label: string
}

/**
 * Each candidate's quadrant on a matrix map (docs/03 §13.5 `quadrantOf`): the items its filter takes – types,
 * statuses, templates, and the shares for `groupIds` – with both values; items without are left out (*Not yet
 * assessed*).
 */
export function quadrantsOf(
  matrix: MatrixSettings,
  items: readonly MapItemRow[],
  defsByKey: Readonly<Record<string, FieldDefinition>>,
  valuesByItem: ReadonlyMap<string, Readonly<Record<string, unknown>>>,
  sharedWith: (itemId: string, groupIds: readonly string[]) => boolean,
): Map<string, ItemQuadrant> {
  const groups = matrix.filter?.groupIds ?? []
  const candidates = matrixCandidates(items, matrix.filter).filter((i) => !groups.length || sharedWith(i.id, groups))
  const out = new Map<string, ItemQuadrant>()
  for (const v of matrixValues(candidates, matrix, defsByKey, valuesByItem)) {
    const q = quadrantOf(v.x, v.y, matrix)
    if (q) out.set(v.id, q)
  }
  return out
}

/** An item's value of a field as a number (for the quick edit's read-only scores). */
export function fieldNumber(
  key: string,
  defsByKey: Readonly<Record<string, FieldDefinition>>,
  values: Readonly<Record<string, unknown>>,
): number | null {
  const def = defsByKey[key]
  return def ? numericValue(def, defsByKey, values) : null
}

/**
 * The fields the quick edit offers on a map (doc 01 §12.5 *key fields and scores*): the card badges and a matrix'
 * axes and size; a score with the inputs it is computed from (scores of scores too), each once, in that order.
 */
export function keyFields(
  keys: readonly string[],
  defsByKey: Readonly<Record<string, FieldDefinition>>,
): FieldDefinition[] {
  const out: FieldDefinition[] = []
  const seen = new Set<string>()
  const visit = (key: string, depth: number) => {
    const def = defsByKey[key]
    if (!def || seen.has(key) || depth > 4) return
    seen.add(key)
    out.push(def)
    if (def.type === 'score') for (const input of def.config?.inputs ?? []) visit(input, depth + 1)
  }
  for (const key of keys) visit(key, 0)
  return out
}
