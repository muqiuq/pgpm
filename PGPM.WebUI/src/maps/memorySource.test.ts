import { describe, expect, it } from 'vitest'
import { watch } from 'vue'
import { uuidv7 } from './ids'
import { createMemorySource } from './memorySource'
import { BASE, C1, MAP_ID, R1, seed } from './testFixtures'

describe('the in-memory data source', () => {
  it('publishes a batch once, at its end, with one batch id', async () => {
    const source = createMemorySource(seed())
    let published = 0
    watch(source.elements, () => published++, { flush: 'sync' })
    await source.batch(() => {
      void source.patch('map_elements', C1, { x: { old: 0, new: 5 } })
      void source.patch('map_elements', R1, { x: { old: 0, new: 6 } })
      void source.remove('map_elements', C1)
    })
    expect(published).toBe(1)
    expect(source.log.value.map((m) => m.op)).toEqual(['patch', 'patch', 'delete'])
    expect(new Set(source.log.value.map((m) => m.batch)).size).toBe(1)
    expect(source.elements.value.find((e) => e.id === R1)!.x).toBe(6)
  })

  it('keeps the identity of rows that didn’t change', () => {
    const source = createMemorySource(seed())
    const before = source.elements.value.find((e) => e.id === R1)
    void source.patch('map_elements', C1, { x: { old: 0, new: 5 } })
    expect(source.elements.value.find((e) => e.id === R1)).toBe(before)
  })

  it('applies dotted keys to jsonb members and patches the map', async () => {
    const source = createMemorySource(seed())
    await source.patch('map_elements', R1, { 'style.fill': { old: undefined, new: 'pink' } })
    expect(source.elements.value.find((e) => e.id === R1)!.style).toEqual({ fill: 'pink' })
    await source.patch('maps', MAP_ID, { 'settings.grid': { old: undefined, new: true } })
    expect(source.map.value!.settings).toEqual({ gridSize: 20, grid: true })
  })

  it('refuses writes with read access, and rows it doesn’t know', async () => {
    const source = createMemorySource(seed({ access: 'read' }))
    await expect(source.remove('map_elements', C1)).rejects.toThrow(/Read access/)
    source.setAccess('edit')
    await expect(source.patch('map_elements', 'nope', {})).rejects.toThrow()
    await expect(
      source.create('map_layers', { id: BASE, map_id: MAP_ID, name: 'x', sort_order: 'a', is_visible: true }),
    ).rejects.toThrow(/exists/)
    expect(source.log.value).toHaveLength(0)
  })
})

describe('uuidv7', () => {
  it('makes version 7 ids that sort in creation order, also within a millisecond', () => {
    const ids = Array.from({ length: 50 }, () => uuidv7(1_764_000_000_000))
    for (const id of ids) expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect([...ids].sort()).toEqual(ids)
    expect(new Set(ids).size).toBe(50)
  })
})
