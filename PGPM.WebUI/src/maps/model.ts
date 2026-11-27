import { computed, type Ref } from 'vue'
import { compareKeyThenId } from '@/domain/fractional'
import { jsonEqual, type Row } from '@/local/outbox'
import { cardModel, numericValue, type CardModel, type FieldDefinition } from './cards'
import type { MapDataSource, MapTable } from './dataSource'
import type { Rect } from './geometry'
import { frameOf, lineGeometry, shapeOf, storedEnds, type LineEnd, type LineGeometry } from './scene'
import type {
  DodItemRow,
  ElementRow,
  ItemRow,
  LayerRow,
  MapSettings,
  MilestoneRow,
  PersonRow,
  StatusRow,
} from './types'

/**
 * The map's rows looked up and derived (the canvas', the side panel's and the editor's shared view of a data source):
 * layers in order, elements by layer in z order, items, statuses, milestones, field values by key, persons, and the
 * card models – rebuilt when their rows change, each kept as the same object while it shows the same, so that only the
 * cards that changed re-render. `virtual`: elements without rows (a matrix map's cards) on layers of their own.
 */

export interface VirtualLayer {
  layer: LayerRow
  elements: readonly ElementRow[]
}

export function compareElements(a: ElementRow, b: ElementRow): number {
  return compareKeyThenId(a.z, a.id, b.z, b.id)
}

/** Field definitions by key and each item's field values by key (cards, matrices, the filter's score range). */
export function createFieldLookups(source: Pick<MapDataSource, 'fieldDefinitions' | 'fieldValues'>) {
  const defsById = computed(() => new Map(source.fieldDefinitions.value.map((d) => [d.id, d])))
  const defsByKey = computed(() => {
    const byKey: Record<string, FieldDefinition> = {}
    for (const d of source.fieldDefinitions.value) {
      byKey[d.key] = { id: d.id, key: d.key, label: d.label, type: d.type, config: d.config, isArchived: d.is_archived }
    }
    return byKey
  })
  const valuesByItem = computed(() => {
    const byItem = new Map<string, Record<string, unknown>>()
    for (const v of source.fieldValues.value) {
      const def = defsById.value.get(v.field_id)
      if (!def) continue
      let values = byItem.get(v.item_id)
      if (!values) byItem.set(v.item_id, (values = {}))
      values[def.key] = v.value
    }
    return byItem
  })
  return { defsByKey, valuesByItem }
}

export function createMapModel(
  source: MapDataSource,
  options: { now: Readonly<Ref<number>>; virtual?: Readonly<Ref<VirtualLayer | null>> },
) {
  const map = computed(() => source.map.value)
  const mapId = computed(() => map.value?.id ?? '')
  const settings = computed<MapSettings>(() => map.value?.settings ?? {})

  /** The stored layers, bottom first. */
  const layers = computed(() =>
    [...source.layers.value].sort((a, b) => compareKeyThenId(a.sort_order, a.id, b.sort_order, b.id)),
  )
  const layerById = computed(() => new Map(layers.value.map((l) => [l.id, l])))

  const elementsById = computed(() => {
    const byId = new Map<string, ElementRow>()
    for (const e of options.virtual?.value?.elements ?? []) byId.set(e.id, e)
    for (const e of source.elements.value) byId.set(e.id, e)
    return byId
  })

  /** Each stored layer's elements in drawing order. */
  const elementsByLayer = computed(() => {
    const byLayer = new Map<string, ElementRow[]>()
    for (const l of layers.value) byLayer.set(l.id, [])
    for (const e of source.elements.value) byLayer.get(e.layer_id)?.push(e)
    for (const list of byLayer.values()) list.sort(compareElements)
    return byLayer
  })

  /** Every element in drawing order: the virtual layer below the stored ones. */
  const drawingOrder = computed(() => {
    const all: ElementRow[] = [...(options.virtual?.value?.elements ?? [])]
    for (const l of layers.value) all.push(...(elementsByLayer.value.get(l.id) ?? []))
    return all
  })

  const itemsById = computed(() => new Map<string, ItemRow>(source.items.value.map((i) => [i.id, i])))
  const statusesById = computed(() => new Map<string, StatusRow>(source.statuses.value.map((s) => [s.id, s])))
  const milestonesById = computed(() => new Map<string, MilestoneRow>(source.milestones.value.map((m) => [m.id, m])))
  const milestonesByProject = computed(() => {
    const byProject = new Map<string, MilestoneRow[]>()
    for (const m of source.milestones.value) {
      const list = byProject.get(m.item_id)
      if (list) list.push(m)
      else byProject.set(m.item_id, [m])
    }
    for (const list of byProject.values()) list.sort((a, b) => compareKeyThenId(a.sort_order, a.id, b.sort_order, b.id))
    return byProject
  })
  const dodByMilestone = computed(() => {
    const byMilestone = new Map<string, DodItemRow[]>()
    for (const d of source.dodItems.value) {
      const list = byMilestone.get(d.milestone_id)
      if (list) list.push(d)
      else byMilestone.set(d.milestone_id, [d])
    }
    for (const list of byMilestone.values())
      list.sort((a, b) => compareKeyThenId(a.sort_order, a.id, b.sort_order, b.id))
    return byMilestone
  })

  const { defsByKey, valuesByItem } = createFieldLookups(source)
  const personsByItem = computed(() => {
    const byItem = new Map<string, Set<string>>()
    for (const a of source.assignments.value) {
      if (!a.item_id) continue
      const set = byItem.get(a.item_id)
      if (set) set.add(a.person_id)
      else byItem.set(a.item_id, new Set([a.person_id]))
    }
    return byItem
  })
  const personsById = computed(() => new Map<string, PersonRow>(source.persons.value.map((p) => [p.id, p])))
  const links = computed(() => source.links.value)

  /** An item's numeric field value (the filter's score range, the matrix axes). */
  function valueOf(itemId: string, key: string): number | null {
    const def = defsByKey.value[key]
    return def ? numericValue(def, defsByKey.value, valuesByItem.value.get(itemId) ?? {}) : null
  }

  let previousCards = new Map<string, CardModel>()
  /** The card model of every item, the previous object kept while nothing it shows changed. */
  const cards = computed(() => {
    const ctx = {
      statusesById: statusesById.value,
      defsByKey: defsByKey.value,
      valuesByItem: valuesByItem.value,
      personsByItem: new Map([...personsByItem.value].map(([id, set]) => [id, set.size])),
      milestonesByProject: milestonesByProject.value,
      badgeKeys: settings.value.cardBadges ?? [],
      now: options.now.value,
    }
    const next = new Map<string, CardModel>()
    for (const item of source.items.value) {
      const model = cardModel(item, ctx)
      const before = previousCards.get(item.id)
      next.set(item.id, before && jsonEqual(before, model) ? before : model)
    }
    previousCards = next
    return next
  })

  /** The cards of each item on the map: element ids in drawing order. */
  const cardsOfItem = computed(() => {
    const byItem = new Map<string, string[]>()
    for (const e of drawingOrder.value) {
      if (e.kind !== 'item' || !e.item_id) continue
      const list = byItem.get(e.item_id)
      if (list) list.push(e.id)
      else byItem.set(e.item_id, [e.id])
    }
    return byItem
  })

  /** The current row of a map table (for the editor and the undo stack). */
  function rowOf(table: MapTable, id: string): Row | undefined {
    if (table === 'maps') return map.value?.id === id ? map.value : undefined
    if (table === 'map_layers') return layerById.value.get(id)
    const row = elementsById.value.get(id)
    return row && row.layer_id !== options.virtual?.value?.layer.id ? row : undefined
  }

  /** The shape a line end attached to an element meets, with the element's frame now. */
  function endOf(id: string | null | undefined, fallback: LineEnd, frame: (id: string) => Rect | undefined): LineEnd {
    if (!id) return fallback
    const target = elementsById.value.get(id)
    const f = target && frame(id)
    if (!target || !f || target.kind === 'line') return fallback
    return shapeOf(target, f, target.milestone_id ? milestonesById.value.get(target.milestone_id) : undefined)
  }

  /** A line's geometry with the elements' frames given (default: as stored). */
  function lineOf(line: ElementRow, frame: (id: string) => Rect | undefined = storedFrame): LineGeometry {
    const [a, b] = storedEnds(line)
    return lineGeometry(endOf(line.start_element_id, a, frame), endOf(line.end_element_id, b, frame), line.style)
  }

  function storedFrame(id: string): Rect | undefined {
    const e = elementsById.value.get(id)
    return e ? frameOf(e) : undefined
  }

  return {
    map,
    mapId,
    settings,
    layers,
    layerById,
    elementsById,
    elementsByLayer,
    drawingOrder,
    itemsById,
    statusesById,
    milestonesById,
    milestonesByProject,
    dodByMilestone,
    defsByKey,
    valuesByItem,
    personsByItem,
    personsById,
    links,
    cards,
    cardsOfItem,
    valueOf,
    rowOf,
    lineOf,
    storedFrame,
  }
}

export type MapModel = ReturnType<typeof createMapModel>
