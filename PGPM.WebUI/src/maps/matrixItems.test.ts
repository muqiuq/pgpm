import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { createMemorySource } from './memorySource'
import { matrixCandidates, matrixCardId, matrixElements, matrixScene, matrixValues } from './matrixItems'
import { PLOT } from './matrixLayout'
import { createFieldLookups } from './model'
import { ASSESSED, FIELDS, IDEA, ITEMS, NOTE, POC, PROJECT, seed, uid } from './testFixtures'
import type { ItemRow, MatrixSettings } from './types'

const config: MatrixSettings = {
  x: { field: 'feasibility', label: 'Ease', min: 0, max: 10 },
  y: { field: 'impact', label: 'Impact', min: 0, max: 100 },
  quadrants: { tr: 'Stars' },
  filter: { types: ['idea', 'project'] },
}

function lookups(values = seed().fieldValues!) {
  const source = createMemorySource(seed({ fieldValues: values }))
  return { source, ...createFieldLookups(source) }
}

describe('matrix items', () => {
  it('takes the items of the filter: types, statuses, templates', () => {
    expect(matrixCandidates(ITEMS, config.filter).map((i) => i.id)).toEqual([IDEA, PROJECT])
    expect(matrixCandidates(ITEMS, { statusIds: [POC] }).map((i) => i.id)).toEqual([PROJECT])
    const withTemplate: ItemRow[] = [...ITEMS, { id: 'p2', type: 'project', title: 'T', project_template_id: 'tpl' }]
    expect(matrixCandidates(withTemplate, { templateIds: ['tpl'] }).map((i) => i.id)).toEqual(['p2'])
    expect(matrixCandidates(ITEMS, null)).toHaveLength(3)
  })

  it('reads the axes from scores and numbers', () => {
    const { defsByKey, valuesByItem } = lookups()
    const values = matrixValues(ITEMS.slice(0, 2), config, defsByKey.value, valuesByItem.value)
    // feasibility = 4 + 3 (sum of ratings), impact 80; the project has none.
    expect(values).toEqual([
      { id: IDEA, x: 7, y: 80, size: null },
      { id: PROJECT, x: null, y: null, size: null },
    ])
  })

  it('places assessed items in their quadrant and the others in the tray', () => {
    const { defsByKey, valuesByItem } = lookups()
    const scene = matrixScene(ITEMS, config, defsByKey.value, valuesByItem.value)
    expect(scene.layout.cards.map((c) => [c.id, c.quadrant])).toEqual([[IDEA, 'tr']])
    expect(scene.tray.map((i) => i.id)).toEqual([PROJECT])
    const card = scene.layout.cards[0]!
    // x 7 of 0–10, y 80 of 0–100 (up).
    expect(card.targetX).toBeCloseTo(PLOT.minX + 0.7 * (PLOT.maxX - PLOT.minX))
    expect(card.targetY).toBeCloseTo(PLOT.maxY - 0.8 * (PLOT.maxY - PLOT.minY))
    const [el] = matrixElements(scene.layout, 'map')
    expect(el).toMatchObject({ id: matrixCardId(IDEA), kind: 'item', item_id: IDEA, x: card.x, y: card.y })
  })

  it('moves a card when a value changes', () => {
    const { source, defsByKey, valuesByItem } = lookups()
    const before = matrixScene(source.items.value, config, defsByKey.value, valuesByItem.value).layout.cards[0]!
    source.put('field_values', { id: uid('v3'), item_id: IDEA, field_id: uid('f4'), value: 10 })
    const after = matrixScene(source.items.value, config, defsByKey.value, valuesByItem.value).layout.cards[0]!
    expect(after.quadrant).toBe('br')
    expect(after.y).toBeGreaterThan(before.y)
    // An n/a rating rescales the sum: 4 of 5 → 8 of 10.
    source.put('field_values', { id: uid('v2'), item_id: IDEA, field_id: uid('f2'), value: 'na' })
    expect(matrixValues([ITEMS[0]!], config, defsByKey.value, valuesByItem.value)[0]!.x).toBe(8)
  })

  it('leaves out items without both values, keeping their order in the tray', () => {
    const { defsByKey, valuesByItem } = lookups([])
    const items: ItemRow[] = [
      { id: 'b', type: 'idea', title: 'B', status_id: ASSESSED },
      { id: 'a', type: 'idea', title: 'A', status_id: ASSESSED },
    ]
    const scene = matrixScene(items, config, defsByKey.value, valuesByItem.value)
    expect(scene.layout.cards).toHaveLength(0)
    expect(scene.tray.map((i) => i.id)).toEqual(['b', 'a'])
  })

  it('works with the field lookups of a fresh source', () => {
    const fields = createFieldLookups({ fieldDefinitions: ref(FIELDS), fieldValues: ref([]) })
    expect(Object.keys(fields.defsByKey.value)).toContain('feasibility')
    expect(fields.valuesByItem.value.get(NOTE)).toBeUndefined()
  })
})
