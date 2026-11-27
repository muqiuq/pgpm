import { nextTick, ref } from 'vue'
import { createMapEditor } from './editor'
import { createMemorySource, type MemorySeed } from './memorySource'
import { createMapModel } from './model'
import type { ElementRow, FieldDefinitionRow, ItemRow, LayerRow, StatusRow } from './types'

/** Rows for the map tests: a canvas with two layers, cards (two of one item), shapes, a connected line and a text. */

export const uid = (n: number | string) => `00000000-0000-7000-8000-${String(n).padStart(12, '0')}`

export const MAP_ID = uid('ma9')
export const BASE = uid('1a1')
export const TOP = uid('1a2')
export const IDEA = uid('11')
export const PROJECT = uid('12')
export const NOTE = uid('13')
export const ASSESSED = uid('51')
export const POC = uid('52')

export const STATUSES: StatusRow[] = [
  { id: ASSESSED, item_type: 'idea', name: 'Assessed', color: '#8b5cf6', category: 'open', sort_order: 1 },
  {
    id: POC,
    item_type: 'project',
    name: 'Prototype / PoC',
    color: '#f59e0b',
    category: 'active',
    sort_order: 1,
    max_days: 84,
  },
]

export const FIELDS: FieldDefinitionRow[] = [
  { id: uid('f1'), key: 'feas_a', label: 'A', type: 'rating', config: { max: 5 } },
  { id: uid('f2'), key: 'feas_b', label: 'B', type: 'rating', config: { max: 5 } },
  {
    id: uid('f3'),
    key: 'feasibility',
    label: 'Feasibility',
    type: 'score',
    config: { op: 'sum', inputs: ['feas_a', 'feas_b'], na: 'rescale', max: 10 },
  },
  { id: uid('f4'), key: 'impact', label: 'Impact', type: 'number', config: { min: 0, max: 100 } },
]

export const ITEMS: ItemRow[] = [
  { id: IDEA, type: 'idea', number: 7, title: 'Chatbot', color: 'purple', status_id: ASSESSED },
  {
    id: PROJECT,
    type: 'project',
    number: 3,
    title: 'Website',
    color: null,
    status_id: POC,
    status_changed_at: '2026-01-01T00:00:00Z',
  },
  { id: NOTE, type: 'note', number: 1, title: 'Workshop', description: 'Some **notes**', color: 'yellow' },
]

export function layer(id: string, name: string, sort_order: string, extra: Partial<LayerRow> = {}): LayerRow {
  return { id, map_id: MAP_ID, name, sort_order, is_visible: true, is_locked: false, ...extra }
}

export function element(id: string, row: Partial<ElementRow> & Pick<ElementRow, 'kind'>): ElementRow {
  return {
    id,
    map_id: MAP_ID,
    layer_id: BASE,
    z: 'a0',
    x: 0,
    y: 0,
    width: 100,
    height: 50,
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
    ...row,
  }
}

export const C1 = uid('e1')
export const C2 = uid('e2')
export const R1 = uid('e3')
export const E1 = uid('e4')
export const L1 = uid('e5')
export const T1 = uid('e6')
export const P1 = uid('e7')

export function elements(): ElementRow[] {
  return [
    element(C1, { kind: 'item', item_id: IDEA, x: 0, y: 0, width: 200, height: 80, z: 'a0' }),
    element(C2, { kind: 'item', item_id: IDEA, x: 400, y: 0, width: 200, height: 80, z: 'a1' }),
    element(R1, { kind: 'rect', x: 0, y: 300, width: 100, height: 50, z: 'a2', text: 'Box' }),
    element(E1, { kind: 'ellipse', x: 400, y: 300, width: 100, height: 60, z: 'a3' }),
    element(L1, {
      kind: 'line',
      x: 100,
      y: 40,
      width: 1,
      height: 260,
      points: [
        [0, 0],
        [0, 260],
      ],
      start_element_id: C1,
      end_element_id: R1,
      z: 'a4',
      style: { arrowEnd: 'arrow' },
    }),
    element(P1, { kind: 'item', item_id: PROJECT, x: 800, y: 0, width: 200, height: 80, z: 'a5' }),
    element(T1, { kind: 'text', layer_id: TOP, x: 0, y: 500, width: 200, height: 40, z: 'a0', text: 'Hello' }),
  ]
}

export function seed(extra: Partial<MemorySeed> = {}): MemorySeed {
  return {
    map: { id: MAP_ID, kind: 'canvas', name: 'Test map', settings: { gridSize: 20 }, access: 'manage' },
    layers: [layer(BASE, 'Base', 'a0'), layer(TOP, 'Top', 'a1')],
    elements: elements(),
    items: ITEMS,
    statuses: STATUSES,
    fieldDefinitions: FIELDS,
    fieldValues: [
      { id: uid('v1'), item_id: IDEA, field_id: uid('f1'), value: 4 },
      { id: uid('v2'), item_id: IDEA, field_id: uid('f2'), value: 3 },
      { id: uid('v3'), item_id: IDEA, field_id: uid('f4'), value: 80 },
    ],
    ...extra,
  }
}

/** A memory source with its model and editor, as the canvas builds them. */
export function setup(extra: Partial<MemorySeed> = {}, options: { readonly?: boolean } = {}) {
  const source = createMemorySource(seed(extra))
  const model = createMapModel(source, { now: ref(Date.parse('2026-11-27T12:00:00Z')) })
  const errors: unknown[] = []
  const editor = createMapEditor(source, model, {
    readonly: ref(options.readonly ?? false),
    onError: (e) => errors.push(e),
  })
  const row = (id: string) => source.elements.value.find((e) => e.id === id)
  return { source, model, editor, errors, row }
}

/** A pointer event for the tests: jsdom has no PointerEvent, so a MouseEvent with a pointer id. */
export async function fire(target: Element, type: string, x: number, y: number, extra: MouseEventInit = {}) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, ...extra })
  Object.defineProperty(event, 'pointerId', { value: 1 })
  target.dispatchEvent(event)
  await nextTick()
}
