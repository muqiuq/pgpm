import { numericValue, type FieldDefinition } from './cards'
import { layoutMatrix, type MatrixItem, type MatrixLayout } from './matrixLayout'
import type { ElementRow, ItemRow, MatrixSettings } from './types'

/**
 * A matrix map's cards (doc 01 §12.8, docs/03 §13.5): the items matching `maps.matrix.filter` with their values on the
 * two axes (number, rating or score – score.ts) and the bubble size, laid out by matrixLayout.ts. Matrix cards have no
 * rows; they are drawn as virtual item elements on a layer of their own below the free elements.
 */

/** The id of the virtual layer and the prefix of the virtual cards' ids. */
export const MATRIX_LAYER_ID = 'matrix:cards'
export const MATRIX_CARD_PREFIX = 'matrix:'

export function matrixCardId(itemId: string): string {
  return `${MATRIX_CARD_PREFIX}${itemId}`
}

export function isMatrixCard(id: string): boolean {
  return id.startsWith(MATRIX_CARD_PREFIX)
}

/** The items the matrix shows: by type, status and project template (shares are the data source's). */
export function matrixCandidates(items: readonly ItemRow[], filter: MatrixSettings['filter']): ItemRow[] {
  const types = filter?.types?.length ? filter.types : null
  const statuses = filter?.statusIds?.length ? filter.statusIds : null
  const templates = filter?.templateIds?.length ? filter.templateIds : null
  return items.filter(
    (item) =>
      (!types || types.includes(item.type)) &&
      (!statuses || (!!item.status_id && statuses.includes(item.status_id))) &&
      (!templates || (!!item.project_template_id && templates.includes(item.project_template_id))),
  )
}

/** Each item with its values: x, y and size from the configured fields; null where there is none. */
export function matrixValues(
  items: readonly ItemRow[],
  config: MatrixSettings,
  defsByKey: Readonly<Record<string, FieldDefinition>>,
  valuesByItem: ReadonlyMap<string, Readonly<Record<string, unknown>>>,
): MatrixItem[] {
  const value = (key: string | undefined, item: ItemRow) => {
    const def = key ? defsByKey[key] : undefined
    return def ? numericValue(def, defsByKey, valuesByItem.get(item.id) ?? {}) : null
  }
  return items.map((item) => ({
    id: item.id,
    x: value(config.x?.field, item),
    y: value(config.y?.field, item),
    size: config.size?.field ? value(config.size.field, item) : null,
  }))
}

/** The layout's cards as virtual item elements of the matrix layer. */
export function matrixElements(layout: MatrixLayout, mapId: string): ElementRow[] {
  return layout.cards.map((card) => ({
    id: matrixCardId(card.id),
    map_id: mapId,
    layer_id: MATRIX_LAYER_ID,
    kind: 'item',
    z: 'a0',
    x: card.x,
    y: card.y,
    width: card.width,
    height: card.height,
    item_id: card.id,
    config: { size: 'compact', scale: card.scale },
  }))
}

/** Everything a matrix map draws from its items. */
export function matrixScene(
  items: readonly ItemRow[],
  config: MatrixSettings,
  defsByKey: Readonly<Record<string, FieldDefinition>>,
  valuesByItem: ReadonlyMap<string, Readonly<Record<string, unknown>>>,
) {
  const candidates = matrixCandidates(items, config.filter)
  const layout = layoutMatrix(matrixValues(candidates, config, defsByKey, valuesByItem), config)
  const byId = new Map(candidates.map((i) => [i.id, i]))
  return { layout, tray: layout.tray.map((t) => byId.get(t.id)!).filter(Boolean) }
}
