import type { Quadrant } from './matrixLayout'
import type { ElementRow, FilterType, ItemRow, MapFilter } from './types'

/**
 * The visibility filter (doc 01 §12.7), a per-user view setting: by element type (the item types, milestones,
 * drawings), and for item cards by status, assigned person, colour, project template, matrix quadrant and a score
 * range. Hidden elements are not deleted; a line connected to a hidden element is hidden too. Pure functions.
 */

export interface FilterContext {
  itemsById: ReadonlyMap<string, ItemRow>
  /** The persons assigned to an item. */
  personsByItem: ReadonlyMap<string, ReadonlySet<string>>
  /** The item's quadrant on this (matrix) map; null: none. */
  quadrantOf?: (itemId: string) => Quadrant | null
  /** A field's numeric value (score, rating, number) for an item; null: none. */
  valueOf: (itemId: string, fieldKey: string) => number | null
}

/** What the filter calls an element: its item's type, a milestone (also a timeline), or a drawing. */
export function filterTypeOf(el: Pick<ElementRow, 'kind' | 'item_id'>, item?: ItemRow): FilterType | null {
  if (el.kind === 'item') return item?.type ?? null
  if (el.kind === 'milestone' || el.kind === 'timeline') return 'milestone'
  return 'drawing'
}

/** The colour the filter knows an item by: its stored colour, `default` without one. */
export function colorKey(item: Pick<ItemRow, 'color'>): string {
  return item.color || 'default'
}

const restricts = (list: readonly unknown[] | null | undefined): list is readonly unknown[] => Array.isArray(list)

/** Item conditions apply (status, persons, colour, template, quadrant, score): a card must pass them. */
export function hasItemConditions(filter: MapFilter | null | undefined): boolean {
  if (!filter) return false
  return (
    restricts(filter.statusIds) ||
    restricts(filter.personIds) ||
    restricts(filter.colors) ||
    restricts(filter.templateIds) ||
    restricts(filter.quadrants) ||
    !!filter.score?.field
  )
}

/** How many of the filter's conditions are set (the filter button's count). */
export function activeConditions(filter: MapFilter | null | undefined): number {
  if (!filter) return 0
  return (
    [filter.types, filter.statusIds, filter.personIds, filter.colors, filter.templateIds, filter.quadrants].filter(
      restricts,
    ).length + (filter.score?.field ? 1 : 0)
  )
}

/** The item passes the filter's item conditions. */
export function itemMatches(item: ItemRow, filter: MapFilter, ctx: FilterContext): boolean {
  if (restricts(filter.types) && !filter.types.includes(item.type)) return false
  if (restricts(filter.statusIds) && !(item.status_id && filter.statusIds.includes(item.status_id))) return false
  if (restricts(filter.personIds)) {
    const persons = ctx.personsByItem.get(item.id)
    if (!persons || !filter.personIds.some((p) => persons.has(p))) return false
  }
  if (restricts(filter.colors) && !filter.colors.includes(colorKey(item))) return false
  if (restricts(filter.templateIds)) {
    if (!(item.project_template_id && filter.templateIds.includes(item.project_template_id))) return false
  }
  if (restricts(filter.quadrants)) {
    const q = ctx.quadrantOf?.(item.id) ?? null
    if (!q || !filter.quadrants.includes(q)) return false
  }
  const score = filter.score
  if (score?.field) {
    const value = ctx.valueOf(item.id, score.field)
    if (value === null) return false
    if (typeof score.min === 'number' && value < score.min) return false
    if (typeof score.max === 'number' && value > score.max) return false
  }
  return true
}

/** One element on its own (lines: see visibleIds). */
export function elementShown(el: ElementRow, filter: MapFilter | null | undefined, ctx: FilterContext): boolean {
  if (!filter) return true
  const item = el.item_id ? ctx.itemsById.get(el.item_id) : undefined
  const type = filterTypeOf(el, item)
  if (el.kind === 'item') {
    // A card of an item the user can't read: shown as long as nothing asks about the item.
    if (!item) {
      const anyItemType = !restricts(filter.types) || filter.types.some((t) => t !== 'milestone' && t !== 'drawing')
      return anyItemType && !hasItemConditions(filter)
    }
    return itemMatches(item, filter, ctx)
  }
  return !restricts(filter.types) || (type !== null && filter.types.includes(type))
}

/** The ids of the elements shown: each passes the filter, and a line's connected elements are shown too. */
export function visibleIds(
  elements: readonly ElementRow[],
  filter: MapFilter | null | undefined,
  ctx: FilterContext,
): Set<string> {
  const shown = new Set<string>()
  const lines: ElementRow[] = []
  for (const el of elements) {
    if (el.kind === 'line') lines.push(el)
    else if (elementShown(el, filter, ctx)) shown.add(el.id)
  }
  for (const line of lines) {
    if (!elementShown(line, filter, ctx)) continue
    if (line.start_element_id && !shown.has(line.start_element_id)) continue
    if (line.end_element_id && !shown.has(line.end_element_id)) continue
    shown.add(line.id)
  }
  return shown
}
