import { computed, ref, type Ref } from 'vue'
import { backward, forward, keyBetween, keysBetween, toBack, toFront, type Keyed } from '@/domain/fractional'
import type { Row } from '@/local/outbox'
import { parseClipboard, pasteElements, serializeElements } from './clipboard'
import type { MapDataSource } from './dataSource'
import type { Point, Rect } from './geometry'
import { createHistory, type Tx } from './history'
import type { MapModel } from './model'
import { lineRow, round2 } from './scene'
import type { ElementConfig, ElementRow, ElementStyle, LayerRow } from './types'

/**
 * The map's editing operations (doc 01 §12.2–§12.6, docs/03 §13.4) over the data source: each is one undoable
 * command and one write batch, made when the operation is complete (a drag's end, a style change, a text edit). With
 * read access – or `readonly` – nothing is written. The canvas keeps the selection and the drag state; this keeps the
 * rows' rules: z order with fractional keys per layer, moving to a layer, connectors, copy/paste with new ids,
 * deleting elements (never items) with the lines attached to them turned into free ends.
 */

export type ZOrder = 'toFront' | 'forward' | 'backward' | 'toBack'

const REORDER = { toFront, forward, backward, toBack } as const

/** A new element: kind and frame at least; the editor adds id, map, layer and z. */
export type NewElement = Partial<ElementRow> & Pick<ElementRow, 'kind' | 'x' | 'y' | 'width' | 'height'>

export interface EditorOptions {
  readonly?: Readonly<Ref<boolean>>
  onError?: (error: unknown) => void
}

export function createMapEditor(source: MapDataSource, model: MapModel, options: EditorOptions = {}) {
  const history = createHistory(source, { rowOf: model.rowOf, onError: options.onError })
  const canEdit = computed(
    () => !options.readonly?.value && source.access.value !== 'read' && source.map.value !== null,
  )

  /** Where new elements go: the chosen layer, else the topmost visible unlocked one. */
  const chosenLayerId = ref<string | null>(null)
  const activeLayer = computed<LayerRow | null>(() => {
    const chosen = chosenLayerId.value ? model.layerById.value.get(chosenLayerId.value) : undefined
    if (chosen && chosen.is_visible && !chosen.is_locked) return chosen
    const layers = model.layers.value
    for (let i = layers.length - 1; i >= 0; i--) if (layers[i]!.is_visible && !layers[i]!.is_locked) return layers[i]!
    return null
  })

  function run(label: string, fn: (tx: Tx) => void) {
    if (!canEdit.value) return null
    return history.run(label, fn)
  }

  function stored(ids: Iterable<string>): ElementRow[] {
    const out: ElementRow[] = []
    for (const id of ids) {
      const row = model.rowOf('map_elements', id) as ElementRow | undefined
      if (row) out.push(row)
    }
    return out
  }

  /** The elements in drawing order (layer, then z). */
  function inDrawingOrder(rows: ElementRow[]): ElementRow[] {
    const set = new Set(rows.map((r) => r.id))
    return model.drawingOrder.value.filter((e) => set.has(e.id))
  }

  function topZ(layerId: string): string | null {
    const list = model.elementsByLayer.value.get(layerId) ?? []
    return list.length ? list[list.length - 1]!.z : null
  }

  /** A jsonb column's members as dotted keys; a column that is null gets the whole object. */
  function members(row: ElementRow, column: 'style' | 'config', values: Record<string, unknown>) {
    if (row[column] === null || row[column] === undefined) {
      return { [column]: Object.fromEntries(Object.entries(values).filter(([, v]) => v !== undefined)) }
    }
    return Object.fromEntries(Object.entries(values).map(([key, value]) => [`${column}.${key}`, value]))
  }

  /** Adds an element on the active layer (or the one given), on top. The new id; null when nothing was written. */
  function add(element: NewElement, layerId?: string): string | null {
    const layer = layerId ? model.layerById.value.get(layerId) : activeLayer.value
    if (!layer || !canEdit.value) return null
    const id = source.newId()
    const row: ElementRow = {
      item_id: null,
      milestone_id: null,
      project_id: null,
      file_id: null,
      text: null,
      points: null,
      start_element_id: null,
      end_element_id: null,
      style: {},
      config: null,
      ...element,
      x: round2(element.x),
      y: round2(element.y),
      width: Math.max(1, round2(element.width)),
      height: Math.max(1, round2(element.height)),
      id,
      map_id: model.mapId.value,
      layer_id: layer.id,
      z: keyBetween(topZ(layer.id), null),
    }
    return run(`Add ${element.kind}`, (tx) => tx.create('map_elements', row)) ? id : null
  }

  /** Moves elements to these top-left corners: one patch per element (a drag's end, a nudge). */
  function move(positions: ReadonlyMap<string, Point>, label = 'Move') {
    run(label, (tx) => {
      for (const [id, p] of positions) tx.patch('map_elements', id, { x: round2(p.x), y: round2(p.y) })
    })
  }

  function setFrame(id: string, frame: Rect, label = 'Resize') {
    run(label, (tx) =>
      tx.patch('map_elements', id, {
        x: round2(frame.x),
        y: round2(frame.y),
        width: Math.max(1, round2(frame.width)),
        height: Math.max(1, round2(frame.height)),
      }),
    )
  }

  /** A line's stored form at its drawn ends, detached where asked. */
  function freeLine(line: ElementRow, detach: { start: boolean; end: boolean }): Row {
    const geometry = model.lineOf(line)
    return {
      ...lineRow(geometry.start, geometry.end),
      ...(detach.start ? { start_element_id: null } : {}),
      ...(detach.end ? { end_element_id: null } : {}),
    }
  }

  /** Removes elements from the map (never their items); lines attached to them keep their place as free ends. */
  function remove(ids: Iterable<string>) {
    const rows = stored(ids)
    if (!rows.length) return
    const gone = new Set(rows.map((r) => r.id))
    run(rows.length === 1 ? 'Delete' : `Delete ${rows.length} elements`, (tx) => {
      for (const line of source.elements.value) {
        if (line.kind !== 'line' || gone.has(line.id)) continue
        const start = !!line.start_element_id && gone.has(line.start_element_id)
        const end = !!line.end_element_id && gone.has(line.end_element_id)
        if (start || end) tx.patch('map_elements', line.id, freeLine(line, { start, end }))
      }
      for (const row of rows) tx.remove('map_elements', row.id)
    })
  }

  /** Bring to front / forward, send backward / to back – within each element's layer, writing the rows that move. */
  function reorder(op: ZOrder, ids: Iterable<string>) {
    const rows = stored(ids)
    const byLayer = new Map<string, string[]>()
    for (const r of rows) byLayer.set(r.layer_id, [...(byLayer.get(r.layer_id) ?? []), r.id])
    const labels: Record<ZOrder, string> = {
      toFront: 'Bring to front',
      forward: 'Bring forward',
      backward: 'Send backward',
      toBack: 'Send to back',
    }
    run(labels[op], (tx) => {
      for (const [layerId, selected] of byLayer) {
        const keyed: Keyed[] = (model.elementsByLayer.value.get(layerId) ?? []).map((e) => ({ id: e.id, key: e.z }))
        for (const [id, z] of REORDER[op](keyed, selected)) tx.patch('map_elements', id, { z })
      }
    })
  }

  /** Moves elements to another layer, on top of it, in their order. */
  function moveToLayer(ids: Iterable<string>, layerId: string) {
    const layer = model.layerById.value.get(layerId)
    const rows = inDrawingOrder(stored(ids)).filter((r) => r.layer_id !== layerId)
    if (!layer || !rows.length) return
    const keys = keysBetween(topZ(layerId), null, rows.length)
    run(`Move to ${layer.name}`, (tx) => {
      rows.forEach((r, i) => tx.patch('map_elements', r.id, { layer_id: layerId, z: keys[i]! }))
    })
  }

  /** Style keys (`style.fill`, …) for every element; `undefined` removes one (back to the default). */
  function setStyle(ids: Iterable<string>, style: Partial<ElementStyle>) {
    const rows = stored(ids)
    run('Change style', (tx) => {
      for (const r of rows) tx.patch('map_elements', r.id, members(r, 'style', style))
    })
  }

  function setConfig(ids: Iterable<string>, config: Partial<ElementConfig>) {
    const rows = stored(ids)
    run('Change settings', (tx) => {
      for (const r of rows) tx.patch('map_elements', r.id, members(r, 'config', config))
    })
  }

  function setText(id: string, text: string) {
    run('Edit text', (tx) => tx.patch('map_elements', id, { text: text === '' ? null : text }))
  }

  /**
   * Puts a line's end at a point, attached to an element (it follows it) or free. The stored points stay the
   * fallback: the drawn ends as they are now.
   */
  function setLineEnd(lineId: string, end: 'start' | 'end', point: Point, targetId: string | null) {
    const line = stored([lineId])[0]
    if (!line || line.kind !== 'line' || targetId === lineId) return
    const geometry = model.lineOf(line)
    const a = end === 'start' ? point : geometry.start
    const b = end === 'end' ? point : geometry.end
    run(targetId ? 'Connect' : 'Move line end', (tx) =>
      tx.patch('map_elements', lineId, {
        ...lineRow(a, b),
        [end === 'start' ? 'start_element_id' : 'end_element_id']: targetId,
      }),
    )
  }

  /** The elements as clipboard JSON: lines with their drawn ends, so that a free end lands where it was. */
  function copy(ids: Iterable<string>): string | null {
    const rows = inDrawingOrder(stored(ids))
    if (!rows.length) return null
    const set = new Set(rows.map((r) => r.id))
    const out = rows.map((r) => {
      if (r.kind !== 'line') return r
      const start = !!r.start_element_id && !set.has(r.start_element_id)
      const end = !!r.end_element_id && !set.has(r.end_element_id)
      return { ...r, ...freeLine(r, { start, end }) } as ElementRow
    })
    return serializeElements(model.mapId.value, out)
  }

  /** Pastes clipboard JSON on the active layer: new ids, item cards of the same items. The new ids; [] when none. */
  function paste(text: string, at?: Point | null): string[] {
    const data = parseClipboard(text)
    const layer = activeLayer.value
    if (!data || !layer || !canEdit.value) return []
    const sameMap = data.mapId === model.mapId.value
    const rows = pasteElements(data.elements, {
      mapId: model.mapId.value,
      layerId: layer.id,
      topZ: topZ(layer.id),
      newId: () => source.newId(),
      at: at ?? null,
      offset: sameMap ? { x: 20, y: 20 } : { x: 0, y: 0 },
    })
    run(rows.length === 1 ? 'Paste' : `Paste ${rows.length} elements`, (tx) => {
      for (const r of rows) tx.create('map_elements', r)
    })
    return rows.map((r) => r.id)
  }

  function duplicate(ids: Iterable<string>): string[] {
    const text = copy(ids)
    return text ? paste(text) : []
  }

  // --- layers ------------------------------------------------------------------------------------------------------

  function addLayer(name = 'Layer'): string | null {
    const layers = model.layers.value
    const id = source.newId()
    const row: LayerRow = {
      id,
      map_id: model.mapId.value,
      name,
      sort_order: keyBetween(layers.length ? layers[layers.length - 1]!.sort_order : null, null),
      is_visible: true,
      is_locked: false,
    }
    if (!run('Add layer', (tx) => tx.create('map_layers', row))) return null
    chosenLayerId.value = id
    return id
  }

  function patchLayer(id: string, values: Partial<LayerRow>, label: string) {
    run(label, (tx) => tx.patch('map_layers', id, values))
  }

  /** One place up (towards the top) or down. */
  function moveLayer(id: string, direction: 'up' | 'down') {
    const keyed = model.layers.value.map((l) => ({ id: l.id, key: l.sort_order }))
    const changes = (direction === 'up' ? forward : backward)(keyed, [id])
    run(direction === 'up' ? 'Move layer up' : 'Move layer down', (tx) => {
      for (const [layerId, sort_order] of changes) tx.patch('map_layers', layerId, { sort_order })
    })
  }

  /**
   * Deletes a layer (never the last one): its elements move to the layer below (the one above for the bottom layer),
   * on top of it, or are deleted with it.
   */
  function deleteLayer(id: string, mode: 'move' | 'delete') {
    const layers = model.layers.value
    const index = layers.findIndex((l) => l.id === id)
    if (index < 0 || layers.length < 2) return
    const target = layers[index > 0 ? index - 1 : 1]!
    const elements = model.elementsByLayer.value.get(id) ?? []
    run('Delete layer', (tx) => {
      if (mode === 'move') {
        const keys = keysBetween(topZ(target.id), null, elements.length)
        elements.forEach((e, i) => tx.patch('map_elements', e.id, { layer_id: target.id, z: keys[i]! }))
      } else {
        const gone = new Set(elements.map((e) => e.id))
        for (const line of source.elements.value) {
          if (line.kind !== 'line' || gone.has(line.id)) continue
          const start = !!line.start_element_id && gone.has(line.start_element_id)
          const end = !!line.end_element_id && gone.has(line.end_element_id)
          if (start || end) tx.patch('map_elements', line.id, freeLine(line, { start, end }))
        }
        for (const e of elements) tx.remove('map_elements', e.id)
      }
      tx.remove('map_layers', id)
    })
    if (chosenLayerId.value === id) chosenLayerId.value = target.id
  }

  /** Map properties: name, description, `settings.*` (dotted keys). */
  function patchMap(values: Record<string, unknown>, label = 'Change map') {
    const map = source.map.value
    if (!map) return
    run(label, (tx) => tx.patch('maps', map.id, values))
  }

  return {
    canEdit,
    chosenLayerId,
    activeLayer,
    history,
    add,
    move,
    setFrame,
    remove,
    reorder,
    moveToLayer,
    setStyle,
    setConfig,
    setText,
    setLineEnd,
    copy,
    paste,
    duplicate,
    addLayer,
    renameLayer: (id: string, name: string) => patchLayer(id, { name }, 'Rename layer'),
    setLayerVisible: (id: string, is_visible: boolean) =>
      patchLayer(id, { is_visible }, is_visible ? 'Show layer' : 'Hide layer'),
    setLayerLocked: (id: string, is_locked: boolean) =>
      patchLayer(id, { is_locked }, is_locked ? 'Lock layer' : 'Unlock layer'),
    moveLayer,
    deleteLayer,
    patchMap,
    undo: () => (canEdit.value ? history.undo() : null),
    redo: () => (canEdit.value ? history.redo() : null),
  }
}

export type MapEditor = ReturnType<typeof createMapEditor>
