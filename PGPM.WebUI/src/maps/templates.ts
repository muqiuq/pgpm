import { withoutSyncColumns } from './clipboard'
import { uuidv7 } from './ids'
import type { ElementKind, ElementRow, LayerRow, MapRow } from './types'

/**
 * *New map from template* (doc 01 §12.9, docs/03 §13.6): runs on the client, also offline. The template's layers and
 * drawing elements are copied with new ids – their fractional keys kept, so the order stays – and the template's item
 * cards, milestones and timelines are not (they belong to the template's content, not its drawing). Lines keep their
 * connections among the copied elements; a connection to something not copied is dropped (the stored points remain).
 */

/** Element kinds a template doesn't pass on. */
export const NOT_COPIED: readonly ElementKind[] = ['item', 'milestone', 'timeline']

export interface InstantiateOptions {
  /** The new map's id. */
  mapId: string
  newId?: () => string
}

export function instantiateTemplate(
  template: Pick<MapRow, 'id'>,
  elements: readonly ElementRow[],
  layers: readonly LayerRow[],
  options: InstantiateOptions,
): { layers: LayerRow[]; elements: ElementRow[] } {
  const newId = options.newId ?? uuidv7
  const own = layers.filter((l) => l.map_id === template.id)
  const layerIds = new Map(own.map((l) => [l.id, newId()]))
  const newLayers: LayerRow[] = own.map((l) => ({
    ...withoutSyncColumns(l),
    id: layerIds.get(l.id)!,
    map_id: options.mapId,
  }))
  // Every map has a layer (data model §9).
  if (!newLayers.length) {
    newLayers.push({
      id: newId(),
      map_id: options.mapId,
      name: 'Base',
      sort_order: 'a0',
      is_visible: true,
      is_locked: false,
    })
  }
  const copied = elements.filter(
    (e) => e.map_id === template.id && layerIds.has(e.layer_id) && !NOT_COPIED.includes(e.kind),
  )
  const elementIds = new Map(copied.map((e) => [e.id, newId()]))
  const newElements: ElementRow[] = copied.map((e) => ({
    ...withoutSyncColumns(e),
    id: elementIds.get(e.id)!,
    map_id: options.mapId,
    layer_id: layerIds.get(e.layer_id)!,
    start_element_id: e.start_element_id ? (elementIds.get(e.start_element_id) ?? null) : null,
    end_element_id: e.end_element_id ? (elementIds.get(e.end_element_id) ?? null) : null,
  }))
  return { layers: newLayers, elements: newElements }
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T

/** The new map's row: the template's kind, settings, matrix and default view, `template_source_id` set. */
export function mapFromTemplate(
  template: MapRow,
  options: { id: string; name?: string; ownerUserId?: string | null },
): MapRow {
  return {
    id: options.id,
    kind: template.kind,
    name: options.name ?? template.name,
    description: template.description ?? null,
    owner_user_id: options.ownerUserId ?? null,
    settings: clone(template.settings ?? {}),
    matrix: template.matrix ? clone(template.matrix) : null,
    default_view: template.default_view ? clone(template.default_view) : null,
    is_template: false,
    template_source_id: template.id,
  }
}
