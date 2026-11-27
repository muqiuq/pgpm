import { describe, expect, it } from 'vitest'
import { watch } from 'vue'
import { until } from '@/test/memoryDb'
import type { MapDataSource, MapTable } from './dataSource'
import type { MemorySeed } from './memorySource'
import { BASE, C1, E1, MAP_ID, R1, seed } from './testFixtures'
import type { Access } from './types'

/**
 * What every MapDataSource does (dataSource.ts), as tests run against each implementation: the in-memory source
 * (memorySource.test.ts) and the local database's (localSource.test.ts). A harness makes a source from the fixtures'
 * rows and tells the writes it made, as the outbox would get them.
 */

export interface Written {
  batch: string | null
  table: MapTable | string
  op: 'insert' | 'patch' | 'delete'
  id: string
}

export interface SourceHarness {
  source: MapDataSource
  /** The writes so far on the map's tables, in order. */
  written(): Promise<Written[]>
  setAccess(access: Access): Promise<void>
}

export type MakeSource = (seed: MemorySeed) => Promise<SourceHarness>

/** Lets asynchronous publication (a live query) catch up. */
const pause = (ms = 40) => new Promise((r) => setTimeout(r, ms))

export function describeDataSource(name: string, make: MakeSource) {
  describe(`${name}: the MapDataSource contract`, () => {
    it('publishes a batch once, at its end, with one batch id', async () => {
      const { source, written } = await make(seed())
      await until(() => source.elements.value.length > 0, 'the elements')
      let published = 0
      watch(source.elements, () => published++, { flush: 'sync' })
      await source.batch(() => {
        void source.patch('map_elements', C1, { x: { old: 0, new: 5 } })
        void source.patch('map_elements', R1, { x: { old: 0, new: 6 } })
        void source.remove('map_elements', E1)
      })
      await until(() => source.elements.value.find((e) => e.id === R1)?.x === 6, 'the batch')
      await pause()
      expect(published).toBe(1)
      expect(source.elements.value.find((e) => e.id === C1)!.x).toBe(5)
      expect(source.elements.value.find((e) => e.id === E1)).toBeUndefined()
      const log = await written()
      expect(log.map((m) => m.op)).toEqual(['patch', 'patch', 'delete'])
      expect(new Set(log.map((m) => m.batch)).size).toBe(1)
    })

    it('settles a batch whose function is async, and joins a batch inside it', async () => {
      const { source, written } = await make(seed())
      await until(() => source.elements.value.length > 0, 'the elements')
      const answer = await source.batch(async () => {
        void source.patch('map_elements', C1, { y: { old: 0, new: 1 } })
        await Promise.resolve()
        void source.batch(() => source.patch('map_elements', R1, { y: { old: 300, new: 2 } }))
        return 42
      })
      expect(answer).toBe(42)
      await until(() => source.elements.value.find((e) => e.id === R1)?.y === 2, 'the batch')
      const log = await written()
      expect(log).toHaveLength(2)
      expect(new Set(log.map((m) => m.batch)).size).toBe(1)
    })

    it('keeps the identity of rows that didn’t change', async () => {
      const { source } = await make(seed())
      await until(() => source.elements.value.length > 0, 'the elements')
      const before = source.elements.value.find((e) => e.id === R1)
      const card = source.elements.value.find((e) => e.id === C1)
      await source.patch('map_elements', C1, { x: { old: 0, new: 5 } })
      await until(() => source.elements.value.find((e) => e.id === C1)?.x === 5, 'the patch')
      expect(source.elements.value.find((e) => e.id === R1)).toBe(before)
      expect(source.elements.value.find((e) => e.id === C1)).not.toBe(card)
    })

    it('applies dotted keys to jsonb members and patches the map', async () => {
      const { source } = await make(seed())
      await until(() => source.elements.value.length > 0 && !!source.map.value, 'the map')
      await source.patch('map_elements', R1, { 'style.fill': { old: undefined, new: 'pink' } })
      await until(() => !!source.elements.value.find((e) => e.id === R1)?.style?.fill, 'the style')
      expect(source.elements.value.find((e) => e.id === R1)!.style).toEqual({ fill: 'pink' })
      await source.patch('maps', MAP_ID, { 'settings.grid': { old: undefined, new: true } })
      await until(() => source.map.value?.settings?.grid === true, 'the map patch')
      expect(source.map.value!.settings).toEqual({ gridSize: 20, grid: true })
    })

    it('refuses writes with read access, and rows it doesn’t know', async () => {
      const { source, written, setAccess } = await make(seed({ access: 'read' }))
      await until(() => source.elements.value.length > 0 && source.access.value === 'read', 'the map')
      await expect(source.remove('map_elements', C1)).rejects.toThrow(/Read access/)
      await setAccess('edit')
      await until(() => source.access.value === 'edit', 'edit access')
      await expect(source.patch('map_elements', 'nope', { x: { old: 0, new: 1 } })).rejects.toThrow()
      await expect(source.remove('map_elements', 'nope')).rejects.toThrow()
      await expect(
        source.create('map_layers', { id: BASE, map_id: MAP_ID, name: 'x', sort_order: 'a', is_visible: true }),
      ).rejects.toThrow(/exists/)
      expect(await written()).toHaveLength(0)
    })

    it('creates rows the reads show, and keeps the personal view', async () => {
      const { source } = await make(seed())
      await until(() => source.layers.value.length === 2, 'the layers')
      const id = source.newId()
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7/)
      await source.create('map_layers', {
        id,
        map_id: MAP_ID,
        name: 'Notes',
        sort_order: 'a2',
        is_visible: true,
        is_locked: false,
      })
      await until(() => source.layers.value.length === 3, 'the new layer')
      expect(source.layers.value.find((l) => l.id === id)).toMatchObject({ name: 'Notes', map_id: MAP_ID })
      await source.setViewSettings({ showLinks: true, viewport: { x: 1, y: 2, k: 0.5 } })
      expect(source.viewSettings.value).toEqual({ showLinks: true, viewport: { x: 1, y: 2, k: 0.5 } })
    })
  })
}
