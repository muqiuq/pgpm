import { describe, expect, it } from 'vitest'
import { parseClipboard, pasteElements, serializeElements } from './clipboard'
import { instantiateTemplate, mapFromTemplate } from './templates'
import { BASE, C1, E1, element, elements, L1, layer, MAP_ID, R1, T1, TOP, uid } from './testFixtures'
import type { MapRow } from './types'

const template: MapRow = {
  id: MAP_ID,
  kind: 'canvas',
  name: 'Impact field canvas',
  settings: { grid: true, gridSize: 20 },
  is_template: true,
}

describe('instantiateTemplate', () => {
  let n = 0
  const newId = () => uid(`900${++n}`)

  it('copies layers and drawings with new ids, keys kept, not item cards', () => {
    const synced = { seq: 12, rev: 3, created_at: 'x', updated_by: 'y', deleted: false }
    const layers = [{ ...layer(BASE, 'Base', 'a0'), ...synced }, layer(TOP, 'Notes', 'a1V', { is_locked: true })]
    const rows = elements().map((e) => ({ ...e, ...synced }))
    const out = instantiateTemplate(template, rows, layers, { mapId: 'new-map', newId })
    expect(out.layers.map((l) => [l.name, l.sort_order, l.is_locked, l.map_id])).toEqual([
      ['Base', 'a0', false, 'new-map'],
      ['Notes', 'a1V', true, 'new-map'],
    ])
    expect(out.layers.every((l) => l.id !== BASE && l.id !== TOP)).toBe(true)
    expect(out.layers[0]).not.toHaveProperty('seq')
    // Cards (C1, C2, P1) stay behind; rect, ellipse, line and text come along.
    expect(out.elements.map((e) => e.kind).sort()).toEqual(['ellipse', 'line', 'rect', 'text'])
    const ids = new Set([C1, R1, E1, L1, T1])
    for (const e of out.elements) {
      expect(ids.has(e.id)).toBe(false)
      expect(e.map_id).toBe('new-map')
      expect(e).not.toHaveProperty('rev')
    }
    const text = out.elements.find((e) => e.kind === 'text')!
    expect(text.layer_id).toBe(out.layers[1]!.id)
    expect(text.z).toBe('a0')
    const rect = out.elements.find((e) => e.kind === 'rect')!
    const line = out.elements.find((e) => e.kind === 'line')!
    // The line's end at the rectangle follows the copy; its start at a card (not copied) is dropped.
    expect(line.end_element_id).toBe(rect.id)
    expect(line.start_element_id).toBeNull()
    expect(line.points).toEqual(rows.find((e) => e.id === L1)!.points)
  })

  it('gives a template without layers a Base layer', () => {
    const out = instantiateTemplate(template, [], [], { mapId: 'm', newId })
    expect(out.layers).toHaveLength(1)
    expect(out.layers[0]).toMatchObject({ name: 'Base', map_id: 'm', is_visible: true })
  })

  it('ignores rows of other maps', () => {
    const foreign = element(uid('x1'), { kind: 'rect', map_id: 'other' })
    const out = instantiateTemplate(template, [foreign], [layer(BASE, 'Base', 'a0')], { mapId: 'm', newId })
    expect(out.elements).toHaveLength(0)
  })

  it('builds the new map row from the template', () => {
    const row = mapFromTemplate(template, { id: 'n', name: 'Workshop 1', ownerUserId: 'u' })
    expect(row).toMatchObject({
      id: 'n',
      kind: 'canvas',
      name: 'Workshop 1',
      owner_user_id: 'u',
      is_template: false,
      template_source_id: MAP_ID,
      settings: { grid: true, gridSize: 20 },
    })
    expect(row.settings).not.toBe(template.settings)
  })
})

describe('clipboard data', () => {
  it('round-trips elements and refuses other text', () => {
    const text = serializeElements(MAP_ID, elements())
    expect(parseClipboard(text)?.elements).toHaveLength(7)
    expect(parseClipboard('hello')).toBeNull()
    expect(parseClipboard('{"pgpm":"other"}')).toBeNull()
    expect(parseClipboard('{"pgpm":"elements","elements":[{"id":1}]}')).toBeNull()
  })

  it('pastes with new ids and keys above the layer’s top, in order', () => {
    let n = 0
    const rows = pasteElements(elements().slice(0, 3), {
      mapId: 'm2',
      layerId: 'l2',
      topZ: 'a5',
      newId: () => `new-${++n}`,
      offset: { x: 10, y: 0 },
    })
    expect(rows.map((r) => r.id)).toEqual(['new-1', 'new-2', 'new-3'])
    expect(rows.every((r) => r.z > 'a5' && r.layer_id === 'l2' && r.map_id === 'm2')).toBe(true)
    expect(rows[0]!.z < rows[1]!.z && rows[1]!.z < rows[2]!.z).toBe(true)
    expect(rows[1]!.x).toBe(410)
  })
})
