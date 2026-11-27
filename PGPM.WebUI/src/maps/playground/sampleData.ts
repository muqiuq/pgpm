import { keysBetween } from '@/domain/fractional'
import type { MemorySeed } from '../memorySource'
import { lineRow } from '../scene'
import type {
  AssignmentRow,
  DodItemRow,
  ElementRow,
  FieldDefinitionRow,
  FieldValueRow,
  ItemRow,
  ItemType,
  LayerRow,
  LinkRow,
  MilestoneRow,
  PersonRow,
  StatusRow,
} from '../types'

/**
 * The playground's data (`/dev/map`): a demo canvas with every kind of element, a generated stress map (spike S2) and
 * a matrix map. Ids are UUID-shaped and stable (`?focus=item:<id>` works), numbers come from a seeded generator, so a
 * reload draws the same picture.
 */

let counter = 0
/** A stable UUID-shaped id (version 7 nibble, variant 8). */
export function sampleId(kind = 0): string {
  counter++
  return `00000000-${kind.toString(16).padStart(4, '0')}-7000-8000-${counter.toString(16).padStart(12, '0')}`
}

/** mulberry32: a small seeded generator. */
function random(seed: number) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

const status = (
  item_type: ItemType,
  name: string,
  color: string,
  category: StatusRow['category'],
  sort: number,
  extra: Partial<StatusRow> = {},
): StatusRow => ({
  id: sampleId(1),
  item_type,
  name,
  color,
  category,
  sort_order: sort,
  ...extra,
})

export const STATUSES: StatusRow[] = [
  status('idea', 'New', '#64748b', 'open', 1),
  status('idea', 'Elaborating', '#0ea5e9', 'open', 2),
  status('idea', 'Assessed', '#8b5cf6', 'open', 3),
  status('idea', 'Selected', '#16a34a', 'closed', 4),
  status('idea', 'Rejected', '#dc2626', 'closed', 5),
  status('lead', 'New', '#64748b', 'open', 1),
  status('lead', 'Qualified', '#0ea5e9', 'active', 2),
  status('lead', 'Won', '#16a34a', 'closed', 3),
  status('project', 'Draft', '#64748b', 'open', 1),
  status('project', 'Prototype / PoC', '#f59e0b', 'active', 2, { max_days: 84 }),
  status('project', 'Active', '#16a34a', 'active', 3),
  status('project', 'Done', '#0f766e', 'closed', 4),
]

const statusesOf = (type: ItemType) => STATUSES.filter((s) => s.item_type === type)

const field = (
  key: string,
  label: string,
  type: FieldDefinitionRow['type'],
  config: FieldDefinitionRow['config'],
): FieldDefinitionRow => ({
  id: sampleId(2),
  key,
  label,
  type,
  config,
})

export const FIELDS: FieldDefinitionRow[] = [
  field('feas_data', 'Data access', 'rating', { min: 0, max: 5, allowNa: true }),
  field('feas_skills', 'Skills', 'rating', { min: 0, max: 5, allowNa: true }),
  field('feas_infra', 'Infrastructure', 'rating', { min: 0, max: 5, allowNa: true }),
  field('feasibility_index', 'Feasibility', 'score', {
    op: 'sum',
    inputs: ['feas_data', 'feas_skills', 'feas_infra'],
    na: 'rescale',
    max: 15,
    decimals: 0,
  }),
  field('impact_score', 'Impact', 'number', { min: 0, max: 100 }),
  field('business_value_chf', 'Business value', 'number', { unit: 'kCHF' }),
]
const fieldId = (key: string) => FIELDS.find((f) => f.key === key)!.id

export const PERSONS: PersonRow[] = [
  'Anna Muster',
  'Ben Keller',
  'Cleo Rossi',
  'Dario Frei',
  'Eva Lang',
  'Felix Graf',
].map((display_name) => ({ id: sampleId(3), display_name }))

const COLORS = [null, null, 'yellow', 'green', 'blue', 'pink', 'purple', '#fde68a', '#bfdbfe']
const NOTES = [
  '**Workshop** notes: three teams, *twelve* ideas, two quick wins.',
  'Interview with the service desk:\n\n- 40 % of tickets are password resets\n- peak on Mondays',
  'Learning: the PoC needs **real data** from week one.',
  'Open questions\n\n1. Who owns the model?\n2. Budget for GPUs?',
]
const IDEAS = [
  'Chatbot for the service desk',
  'Invoice extraction',
  'Predictive maintenance',
  'Meeting minutes summariser',
  'Contract clause checker',
  'Demand forecasting',
  'Code review assistant',
  'Translation hub',
]

export interface SampleData {
  items: ItemRow[]
  milestones: MilestoneRow[]
  dodItems: DodItemRow[]
  assignments: AssignmentRow[]
  fieldValues: FieldValueRow[]
  links: LinkRow[]
}

/** Items of every type with values, persons, milestones and links. */
export function sampleItems(
  count: number,
  seed = 1,
  types: ItemType[] = ['note', 'idea', 'lead', 'project'],
): SampleData {
  const r = random(seed)
  const pick = <T>(list: readonly T[]) => list[Math.floor(r() * list.length)]!
  const data: SampleData = { items: [], milestones: [], dodItems: [], assignments: [], fieldValues: [], links: [] }
  const numbers: Record<ItemType, number> = { note: 0, idea: 0, lead: 0, project: 0 }
  for (let i = 0; i < count; i++) {
    const type = types[i % types.length]!
    const statuses = statusesOf(type)
    const changed = new Date(Date.now() - Math.floor(r() * 120) * 86_400_000).toISOString()
    const item: ItemRow = {
      id: sampleId(4),
      type,
      number: ++numbers[type],
      title:
        type === 'note'
          ? `Note ${numbers[type]}`
          : type === 'idea'
            ? `${pick(IDEAS)} ${numbers[type]}`
            : type === 'lead'
              ? `Lead ${pick(['Acme', 'Globex', 'Initech', 'Umbrella'])} ${numbers[type]}`
              : `Project ${pick(['Atlas', 'Beacon', 'Comet', 'Delta'])} ${numbers[type]}`,
      description: type === 'note' ? pick(NOTES) : r() < 0.5 ? 'A short **description** of the item.' : null,
      color: type === 'note' ? pick(['yellow', 'green', 'blue', 'pink']) : pick(COLORS),
      status_id: statuses.length ? pick(statuses).id : null,
      status_changed_at: statuses.length ? changed : null,
    }
    data.items.push(item)
    for (const person of PERSONS) {
      if (r() < 0.25) data.assignments.push({ id: sampleId(5), item_id: item.id, person_id: person.id, role: null })
    }
    if (type === 'idea' || type === 'project') {
      const assessed = r() < 0.85
      if (assessed) {
        for (const key of ['feas_data', 'feas_skills', 'feas_infra']) {
          data.fieldValues.push({
            id: sampleId(6),
            item_id: item.id,
            field_id: fieldId(key),
            value: r() < 0.1 ? 'na' : Math.floor(r() * 6),
          })
        }
        data.fieldValues.push({
          id: sampleId(6),
          item_id: item.id,
          field_id: fieldId('impact_score'),
          value: Math.round(r() * 100),
        })
        data.fieldValues.push({
          id: sampleId(6),
          item_id: item.id,
          field_id: fieldId('business_value_chf'),
          value: Math.round(r() * 900) + 50,
        })
      }
    }
    if (type === 'project') {
      const keys = keysBetween(null, null, 5)
      const start = Date.now() - 40 * 86_400_000
      ;['Kick-off', 'G0 · Go for PoC', 'PoC ready', 'G1 · MVP decision', 'Rollout'].forEach((title, k) => {
        const gate = title.startsWith('G')
        const due = new Date(start + k * 30 * 86_400_000).toISOString().slice(0, 10)
        const done = k < 2 && r() < 0.8
        const m: MilestoneRow = {
          id: sampleId(7),
          item_id: item.id,
          kind: gate ? 'gate' : 'milestone',
          title,
          due_date: due,
          status: done ? 'done' : k === 2 ? 'in_progress' : 'planned',
          decision: gate ? (done ? pick(['go', 'iterate', 'go'] as const) : 'pending') : null,
          sort_order: keys[k]!,
        }
        data.milestones.push(m)
        const dodKeys = keysBetween(null, null, 3)
        ;['Criteria agreed', 'Evidence attached', 'Sign-off'].forEach((text, d) =>
          data.dodItems.push({
            id: sampleId(8),
            milestone_id: m.id,
            text,
            state: done ? 'done' : d === 0 && r() < 0.5 ? 'done' : r() < 0.15 ? 'na' : 'open',
            note: null,
            sort_order: dodKeys[d]!,
          }),
        )
      })
    }
  }
  // A few links: notes are learnings of projects, ideas contribute to projects.
  const projects = data.items.filter((i) => i.type === 'project')
  for (const item of data.items) {
    if (!projects.length || r() > 0.3 || item.type === 'project') continue
    data.links.push({
      id: sampleId(9),
      from_item_id: item.id,
      to_item_id: pick(projects).id,
      relation: item.type === 'note' ? 'learning_of' : 'contributes_to',
    })
  }
  return data
}

function layer(mapId: string, name: string, sort_order: string): LayerRow {
  return { id: sampleId(10), map_id: mapId, name, sort_order, is_visible: true, is_locked: false }
}

/** A small SVG as a blob: URL (what the local blob store would hand out). */
export function sampleImage(label: string, hue = 210): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200" viewBox="0 0 320 200">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${hue} 70% 60%)"/><stop offset="1" stop-color="hsl(${hue + 40} 70% 45%)"/></linearGradient></defs>
<rect width="320" height="200" fill="url(#g)"/><circle cx="250" cy="60" r="28" fill="rgba(255,255,255,.6)"/>
<path d="M0 170 L90 100 L160 150 L230 90 L320 160 L320 200 L0 200 Z" fill="rgba(0,0,0,.25)"/>
<text x="20" y="40" font-family="sans-serif" font-size="22" fill="#fff">${label}</text></svg>`
  return URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }))
}

const base = (mapId: string, layerId: string, z: string) => ({
  map_id: mapId,
  layer_id: layerId,
  z,
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
})

/** The demo canvas: about 40 elements of every kind on three layers, a restricted card, links. */
export function demoCanvas(): MemorySeed {
  const mapId = sampleId(11)
  const data = sampleItems(16, 7)
  const [background, main, notes] = [
    layer(mapId, 'Background', 'a0'),
    layer(mapId, 'Base', 'a1'),
    layer(mapId, 'Notes', 'a2'),
  ]
  const zs = keysBetween(null, null, 60)
  let z = 0
  const el = (
    l: LayerRow,
    row: Partial<ElementRow> & Pick<ElementRow, 'kind' | 'x' | 'y' | 'width' | 'height'>,
  ): ElementRow => ({
    ...base(mapId, l.id, zs[z++]!),
    id: sampleId(12),
    ...row,
  })
  const items = data.items
  const byType = (t: ItemType) => items.filter((i) => i.type === t)
  const elements: ElementRow[] = []
  // Swimlanes and a title.
  ;['Discover', 'Assess', 'Build'].forEach((name, i) => {
    elements.push(
      el(background, {
        kind: 'rect',
        x: 0,
        y: i * 320,
        width: 1500,
        height: 300,
        text: null,
        style: { fill: ['blue', 'green', 'yellow'][i]!, stroke: 'none', radius: 12, opacity: 0.6 },
      }),
    )
    elements.push(
      el(background, {
        kind: 'text',
        x: 16,
        y: i * 320 + 10,
        width: 200,
        height: 34,
        text: name,
        style: { fontSize: 22, bold: true },
      }),
    )
  })
  elements.push(
    el(main, {
      kind: 'text',
      x: 0,
      y: -90,
      width: 900,
      height: 60,
      text: 'Innovation board – demo map',
      style: { fontSize: 34, bold: true },
    }),
  )
  // Cards.
  const cards: ElementRow[] = []
  const place = (item: ItemRow, x: number, y: number, expanded = false) => {
    const card = el(main, {
      kind: 'item',
      item_id: item.id,
      x,
      y,
      width: expanded ? 260 : 220,
      height: expanded ? 210 : 92,
      config: { size: expanded ? 'expanded' : 'compact' },
    })
    cards.push(card)
    elements.push(card)
    return card
  }
  byType('idea').forEach((item, i) => place(item, 240 + i * 250, 40 + (i % 2) * 120))
  byType('lead').forEach((item, i) => place(item, 240 + i * 250, 360 + (i % 2) * 110))
  byType('project').forEach((item, i) => place(item, 240 + i * 290, 660, i === 0))
  const repeated = place(byType('idea')[0]!, 1250, 360)
  // A card of an item the user can't read.
  elements.push(
    el(main, {
      kind: 'item',
      item_id: sampleId(13),
      x: 1260,
      y: 120,
      width: 220,
      height: 92,
      config: { size: 'compact' },
    }),
  )
  // Notes on their own layer.
  byType('note').forEach((item, i) => {
    const card = el(notes, {
      kind: 'item',
      item_id: item.id,
      x: 1560,
      y: 20 + i * 150,
      width: 240,
      height: 130,
      config: { size: 'compact' },
    })
    cards.push(card)
    elements.push(card)
  })
  // Milestones and a timeline of the first project.
  const project = byType('project')[0]!
  const ms = data.milestones.filter((m) => m.item_id === project.id)
  elements.push(
    el(main, {
      kind: 'timeline',
      project_id: project.id,
      x: 240,
      y: 990,
      width: 900,
      height: 110,
      config: { orientation: 'horizontal', spacing: 'date', milestoneIds: null, showDates: true, showLabels: true },
    }),
  )
  ms.slice(0, 2).forEach((m, i) =>
    elements.push(
      el(main, { kind: 'milestone', milestone_id: m.id, x: 1180, y: 990 + i * 56, width: 200, height: 36 }),
    ),
  )
  // Connected lines and free ones.
  const connect = (a: ElementRow, b: ElementRow, style: ElementRow['style'], text: string | null = null) => {
    const row = lineRow(
      { x: a.x + a.width / 2, y: a.y + a.height / 2 },
      { x: b.x + b.width / 2, y: b.y + b.height / 2 },
    )
    elements.push(el(main, { kind: 'line', ...row, start_element_id: a.id, end_element_id: b.id, style, text }))
  }
  connect(cards[0]!, cards[5]!, { arrowEnd: 'arrow' }, 'leads to')
  connect(cards[1]!, cards[2]!, { arrowEnd: 'triangle', strokeStyle: 'dashed', stroke: '#2563eb' })
  connect(cards[3]!, repeated, { lineType: 'elbow', arrowEnd: 'arrow', arrowStart: 'dot' })
  connect(cards[6]!, cards[8]!, { lineType: 'elbow', arrowEnd: 'triangle', strokeWidth: 3, stroke: '#16a34a' })
  elements.push(
    el(main, {
      kind: 'line',
      ...lineRow({ x: 60, y: 1150 }, { x: 420, y: 1210 }),
      style: { arrowEnd: 'arrow', strokeStyle: 'dotted', strokeWidth: 3 },
      text: 'free line',
    }),
  )
  // A parking lot and two annotations.
  elements.push(
    el(background, {
      kind: 'rect',
      x: 1540,
      y: 640,
      width: 280,
      height: 180,
      text: 'Parking lot',
      style: { fill: 'grey', strokeStyle: 'dashed', radius: 0, align: 'left' },
    }),
  )
  elements.push(
    el(main, {
      kind: 'text',
      x: 1560,
      y: 830,
      width: 240,
      height: 60,
      text: 'Revisit after the\nQ1 review',
      style: { italic: true, textColor: '#475569' },
    }),
  )
  elements.push(
    el(main, {
      kind: 'text',
      x: 240,
      y: 1240,
      width: 520,
      height: 40,
      text: 'Double-click a text or shape to edit it.',
      style: { fontSize: 14, fill: 'yellow' },
    }),
  )
  elements.push(
    el(main, {
      kind: 'ellipse',
      x: 900,
      y: 1150,
      width: 120,
      height: 120,
      text: '?',
      style: { fill: 'purple', fontSize: 40, bold: true },
    }),
  )
  // An ellipse and an image.
  elements.push(
    el(main, {
      kind: 'ellipse',
      x: 1200,
      y: 840,
      width: 200,
      height: 120,
      text: 'Decision\npoint',
      style: { fill: 'pink', bold: true },
    }),
  )
  const imageId = sampleId(14)
  elements.push(
    el(main, {
      kind: 'image',
      file_id: imageId,
      x: 1500,
      y: 860,
      width: 240,
      height: 150,
      style: { stroke: '#475569', strokeWidth: 1, radius: 8 },
    }),
  )
  return {
    map: {
      id: mapId,
      kind: 'canvas',
      name: 'Demo canvas',
      settings: { grid: true, snap: false, gridSize: 20, cardBadges: ['feasibility_index', 'impact_score'] },
      access: 'manage',
    },
    layers: [background, main, notes],
    elements,
    ...data,
    statuses: STATUSES,
    persons: PERSONS,
    fieldDefinitions: FIELDS,
    files: { [imageId]: sampleImage('Team photo', 200) },
  }
}

/** The stress map of spike S2: `count` elements – mostly cards, shapes, texts and connected lines – on a large grid. */
export function stressMap(count = 1000): MemorySeed {
  const mapId = sampleId(15)
  const data = sampleItems(300, 3)
  const main = layer(mapId, 'Base', 'a0')
  const zs = keysBetween(null, null, count)
  const r = random(42)
  const columns = 40
  const elements: ElementRow[] = []
  const cards: ElementRow[] = []
  for (let i = 0; elements.length < count; i++) {
    const col = i % columns
    const row = Math.floor(i / columns)
    const x = col * 270 + Math.round(r() * 20)
    const y = row * 170 + Math.round(r() * 20)
    const kind = r()
    const common = { ...base(mapId, main.id, zs[elements.length]!), id: sampleId(16) }
    if (kind < 0.6) {
      const item = data.items[Math.floor(r() * data.items.length)]!
      const card: ElementRow = {
        ...common,
        kind: 'item',
        item_id: item.id,
        x,
        y,
        width: 220,
        height: 92,
        config: { size: 'compact' },
      }
      cards.push(card)
      elements.push(card)
    } else if (kind < 0.72) {
      elements.push({
        ...common,
        kind: 'rect',
        x,
        y,
        width: 200,
        height: 110,
        text: `Box ${i}`,
        style: { fill: ['blue', 'green', 'yellow', 'pink'][i % 4]!, radius: 8 },
      })
    } else if (kind < 0.8) {
      elements.push({ ...common, kind: 'ellipse', x, y, width: 180, height: 110, text: 'Topic', style: {} })
    } else if (kind < 0.9) {
      elements.push({
        ...common,
        kind: 'text',
        x,
        y: y + 30,
        width: 220,
        height: 40,
        text: `Sticky thought ${i}`,
        style: { fontSize: 16 },
      })
    } else if (cards.length > 2) {
      const a = cards[cards.length - 1]!
      const b = cards[Math.max(0, cards.length - 1 - Math.ceil(r() * 8))]!
      if (a.id === b.id) continue
      elements.push({
        ...common,
        kind: 'line',
        ...lineRow({ x: a.x, y: a.y }, { x: b.x, y: b.y }),
        start_element_id: a.id,
        end_element_id: b.id,
        style: { arrowEnd: 'arrow', lineType: r() < 0.5 ? 'elbow' : 'straight' },
      })
    }
  }
  return {
    map: {
      id: mapId,
      kind: 'canvas',
      name: `Stress map (${count} elements)`,
      settings: { grid: false, cardBadges: ['feasibility_index'] },
      access: 'edit',
    },
    layers: [main],
    elements,
    ...data,
    statuses: STATUSES,
    persons: PERSONS,
    fieldDefinitions: FIELDS,
  }
}

/** A matrix map: impact × feasibility of `count` ideas and projects, bubble size by business value. */
export function matrixMap(count = 60): MemorySeed {
  const mapId = sampleId(17)
  const data = sampleItems(count, 11, ['idea', 'idea', 'project'])
  const notes = layer(mapId, 'Review notes', 'a0')
  return {
    map: {
      id: mapId,
      kind: 'matrix',
      name: 'Prioritisation matrix',
      settings: { cardBadges: ['feasibility_index', 'impact_score'] },
      matrix: {
        x: { field: 'feasibility_index', label: 'Ease of implementation', min: 0, max: 15 },
        y: { field: 'impact_score', label: 'Business impact', min: 0, max: 100, split: 50 },
        size: { field: 'business_value_chf' },
        quadrants: { tr: 'Star initiatives', br: 'Quick wins', tl: 'Difficult but impactful', bl: 'Limited interest' },
        filter: { types: ['idea', 'project'], statusIds: [], templateIds: [], groupIds: [] },
      },
      access: 'manage',
    },
    layers: [notes],
    elements: [
      {
        ...base(mapId, notes.id, 'a0'),
        id: sampleId(18),
        kind: 'text',
        x: 1380,
        y: 120,
        width: 420,
        height: 60,
        text: 'Review board, 27 Nov: focus on the star initiatives',
        style: { fontSize: 18, bold: true, fill: 'yellow' },
      },
    ],
    ...data,
    statuses: STATUSES,
    persons: PERSONS,
    fieldDefinitions: FIELDS,
  }
}
