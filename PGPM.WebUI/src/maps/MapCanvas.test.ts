import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { until } from '@/test/memoryDb'
import { mountWithUi } from '@/test/mount'
import { SETTLE_MS } from './culling'
import type { MapEditor } from './editor'
import MapCanvas from './MapCanvas.vue'
import { createMemorySource, type MemorySeed, type MemorySource } from './memorySource'
import type { View } from './panZoom'
import { C1, C2, E1, fire, IDEA, L1, P1, R1, seed, uid } from './testFixtures'
import type { ItemRequest } from './tools'
import type { ElementRow } from './types'

interface CanvasApi {
  editor: MapEditor
  selection: string[]
  select: (ids: string[]) => void
  setView: (view: View) => void
  drawnCount: number
}

// jsdom has neither matchMedia, which PrimeVue's Select uses when it mounts, nor ResizeObserver (the canvas' size).
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
})

async function mountCanvas(extra: Partial<MemorySeed> = {}, props: Record<string, unknown> = {}) {
  const source = createMemorySource(seed(extra))
  wrapper = mountWithUi(MapCanvas, { props: { source, ...props }, attachTo: document.body })
  await flushPromises()
  const api = wrapper.vm as unknown as CanvasApi
  api.setView({ x: 0, y: 0, k: 1 })
  await settled()
  await nextTick()
  return { source, api, wrapper }
}

const settled = () => new Promise((resolve) => setTimeout(resolve, SETTLE_MS + 20))

function at(w: VueWrapper, id: string) {
  return w.find(`[data-element-id="${id}"]`)
}

async function drag(
  w: VueWrapper,
  from: string | null,
  a: [number, number],
  b: [number, number],
  extra: MouseEventInit = {},
) {
  const target = (from ? w.find(from) : w.find('svg.surface')).element
  const svg = w.find('svg.surface').element
  await fire(target, 'pointerdown', a[0], a[1], extra)
  await fire(svg, 'pointermove', (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, extra)
  await fire(svg, 'pointermove', b[0], b[1], extra)
  await fire(svg, 'pointerup', b[0], b[1], extra)
}

async function click(w: VueWrapper, id: string, x: number, y: number, extra: MouseEventInit = {}) {
  await fire(at(w, id).element, 'pointerdown', x, y, extra)
  await fire(w.find('svg.surface').element, 'pointerup', x, y, extra)
}

const row = (source: MemorySource, id: string) => source.elements.value.find((e) => e.id === id)

describe('MapCanvas rendering', () => {
  it('draws every element of the visible layers, cards as HTML, a restricted one as such', async () => {
    const restricted = uid('77')
    const { wrapper: w } = await mountCanvas({
      elements: [
        ...seed().elements!,
        { ...seed().elements![0]!, id: restricted, item_id: uid('gone'), x: 1000, y: 1000 } as ElementRow,
      ],
    })
    expect(w.findAll('[data-element-id]')).toHaveLength(8)
    expect(at(w, C1).find('foreignObject').exists()).toBe(true)
    expect(at(w, C1).text()).toContain('I-7')
    expect(at(w, C1).text()).toContain('Chatbot')
    expect(at(w, restricted).classes()).toContain('restricted')
    expect(at(w, L1).find('path.stroke').exists()).toBe(true)
    expect(at(w, R1).text()).toContain('Box')
  })

  it('draws cards as plain rectangles below the level-of-detail zoom', async () => {
    const { api, wrapper: w } = await mountCanvas()
    api.setView({ x: 0, y: 0, k: 0.3 })
    await nextTick()
    expect(at(w, C1).find('foreignObject').exists()).toBe(false)
    expect(at(w, C1).find('text.lod-title').text()).toBe('Chatbot')
    // Far out, not even the title.
    api.setView({ x: 0, y: 0, k: 0.2 })
    await nextTick()
    expect(at(w, C1).find('text.lod-title').exists()).toBe(false)
    // More detail comes once the view has settled.
    api.setView({ x: 0, y: 0, k: 0.5 })
    await nextTick()
    expect(at(w, C1).find('foreignObject').exists()).toBe(false)
    await settled()
    await nextTick()
    expect(at(w, C1).find('foreignObject').exists()).toBe(true)
  })

  it('culls elements far from the viewport on a large map', async () => {
    const many: ElementRow[] = Array.from({ length: 400 }, (_, i) => ({
      ...seed().elements![2]!,
      id: uid(`c${i}`),
      x: (i % 40) * 300,
      y: Math.floor(i / 40) * 300,
      z: `a${i}`,
    }))
    const { api, wrapper: w } = await mountCanvas({ elements: many })
    // 800 × 600 at zoom 1 from (0, 0): the region reaches 400 / 300 beyond each side.
    const drawn = w.findAll('[data-element-id]').length
    expect(drawn).toBeGreaterThan(0)
    expect(drawn).toBeLessThan(40)
    expect(api.drawnCount).toBe(drawn)
    api.setView({ x: -6000, y: -1500, k: 1 })
    await nextTick()
    expect(at(w, uid('c220')).exists()).toBe(true)
    expect(at(w, uid('c0')).exists()).toBe(false)
  })

  it('hides layers and filters', async () => {
    const { source, wrapper: w } = await mountCanvas({ viewSettings: { types: ['drawing'] } })
    expect(at(w, C1).exists()).toBe(false)
    expect(at(w, R1).exists()).toBe(true)
    // A line to a hidden card is hidden.
    expect(at(w, L1).exists()).toBe(false)
    source.setAccess('manage')
    ;(w.vm as unknown as CanvasApi).editor.setLayerVisible(source.layers.value[0]!.id, false)
    await nextTick()
    expect(at(w, R1).exists()).toBe(false)
  })
})

describe('MapCanvas selection and editing', () => {
  it('selects on click, adds with Shift, and opens the side panel', async () => {
    const { api, wrapper: w } = await mountCanvas()
    await click(w, C1, 10, 10)
    expect(api.selection).toEqual([C1])
    await nextTick()
    expect(document.body.textContent).toContain('I-7 Chatbot')
    expect(document.body.textContent).toContain('This item appears 2× on this map')
    await click(w, R1, 10, 310, { shiftKey: true })
    expect(api.selection).toEqual([C1, R1])
    // Shift-click on a selected one takes it out.
    await click(w, C1, 10, 10, { shiftKey: true })
    expect(api.selection).toEqual([R1])
  })

  it('a background click clears the selection', async () => {
    const { api, wrapper: w } = await mountCanvas()
    api.select([C1])
    await drag(w, null, [700, 500], [700, 500])
    expect(api.selection).toEqual([])
  })

  it('selects with the rubber band (Shift + drag on the background)', async () => {
    const { api, wrapper: w } = await mountCanvas()
    await drag(w, null, [-10, -10], [650, 100], { shiftKey: true })
    expect(api.selection.sort()).toEqual([C1, C2].sort())
  })

  it('moves the selection by dragging: only Vue state during the drag, one patch per element at the end', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([C1, R1])
    await nextTick()
    await fire(at(w, C1).element, 'pointerdown', 10, 10)
    const svg = w.find('svg.surface').element
    await fire(svg, 'pointermove', 40, 30)
    await fire(svg, 'pointermove', 60, 50)
    expect(source.log.value).toHaveLength(0)
    expect(at(w, C1).attributes('transform')).toBe('translate(50 40)')
    await fire(svg, 'pointerup', 60, 50)
    expect(source.log.value.map((m) => [m.op, m.id])).toEqual([
      ['patch', C1],
      ['patch', R1],
    ])
    expect(row(source, C1)).toMatchObject({ x: 50, y: 40 })
    expect(row(source, R1)).toMatchObject({ x: 50, y: 340 })
    expect(at(w, C1).attributes('transform')).toBe('translate(50 40)')
  })

  it('resizes with the handles, Shift keeping the ratio', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([R1])
    await nextTick()
    await drag(w, '[data-handle="se"]', [100, 350], [150, 360])
    expect(row(source, R1)).toMatchObject({ x: 0, y: 300, width: 150, height: 60 })
    await drag(w, '[data-handle="se"]', [150, 360], [300, 360], { shiftKey: true })
    expect(row(source, R1)).toMatchObject({ width: 300, height: 120 })
  })

  it('connects a line end by dragging it onto an element', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([L1])
    await nextTick()
    await drag(w, '[data-line-end="end"]', [50, 300], [450, 330])
    expect(row(source, L1)).toMatchObject({ start_element_id: C1, end_element_id: E1 })
  })

  it('deletes with the Delete key and undoes with Ctrl+Z', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([E1])
    const root = w.find('[data-testid="map-canvas"]')
    await root.trigger('keydown', { key: 'Delete' })
    expect(row(source, E1)).toBeUndefined()
    await root.trigger('keydown', { key: 'z', ctrlKey: true })
    await nextTick()
    expect(source.elements.value.filter((e) => e.kind === 'ellipse')).toHaveLength(1)
    expect(api.selection).toHaveLength(1)
  })

  it('edits a shape’s text on Enter; Escape keeps what was typed, one undo takes it back', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([R1])
    await nextTick()
    const root = w.find('[data-testid="map-canvas"]')
    await root.trigger('keydown', { key: 'Enter' })
    await nextTick()
    const editor = w.find('textarea.inline-editor')
    expect(editor.exists()).toBe(true)
    await editor.setValue('Box with a label')
    await editor.trigger('keydown', { key: 'Escape' })
    await nextTick()
    expect(w.find('textarea.inline-editor').exists()).toBe(false)
    expect(row(source, R1)?.text).toBe('Box with a label')
    await root.trigger('keydown', { key: 'z', ctrlKey: true })
    await nextTick()
    expect(row(source, R1)?.text).toBe('Box')
  })

  it('keeps a placed text with what was typed on Escape, and drops one left empty', async () => {
    const { source, wrapper: w } = await mountCanvas()
    const texts = () => source.elements.value.filter((e) => e.kind === 'text')
    const before = texts().length
    await w.find('[data-tool="text"]').trigger('click')
    await drag(w, null, [600, 400], [600, 400])
    await until(() => w.find('textarea.inline-editor').exists())
    await w.find('textarea.inline-editor').setValue('Workshop notes')
    await w.find('textarea.inline-editor').trigger('keydown', { key: 'Escape' })
    await nextTick()
    expect(texts()).toHaveLength(before + 1)
    expect(texts().at(-1)!.text).toBe('Workshop notes')

    await w.find('[data-tool="text"]').trigger('click')
    await drag(w, null, [600, 520], [600, 520])
    await until(() => w.find('textarea.inline-editor').exists())
    await w.find('textarea.inline-editor').trigger('keydown', { key: 'Escape' })
    await nextTick()
    expect(texts()).toHaveLength(before + 1)
  })

  it('leaves Space and the arrows to a focused toolbar button', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    api.select([R1])
    await nextTick()
    const rect = w.find('[data-tool="rect"]')
    await rect.trigger('keydown', { key: 'ArrowDown' })
    await rect.trigger('keydown', { key: ' ' })
    expect(row(source, R1)).toMatchObject({ x: 0, y: 300 })
    expect(source.log.value).toHaveLength(0)
  })

  it('draws a rectangle with its tool and goes back to Select', async () => {
    const { api, source, wrapper: w } = await mountCanvas()
    await w.find('[data-tool="rect"]').trigger('click')
    await drag(w, null, [600, 400], [700, 480])
    const rect = source.elements.value.at(-1)!
    expect(rect).toMatchObject({ kind: 'rect', x: 600, y: 400, width: 100, height: 80 })
    expect(api.selection).toEqual([rect.id])
    expect(w.find('[data-tool="select"]').attributes('aria-pressed')).toBe('true')
  })

  it('asks the host for an item and places its card where the user clicked', async () => {
    const { source, wrapper: w } = await mountCanvas()
    await w.find('[data-tool="idea"]').trigger('click')
    await drag(w, null, [700, 500], [700, 500])
    const [request] = w.emitted('requestItem')![0] as [ItemRequest]
    expect(request.extra.type).toBe('idea')
    expect(request.onMap.has(IDEA)).toBe(true)
    request.place(IDEA)
    const card = source.elements.value.at(-1)!
    expect(card).toMatchObject({ kind: 'item', item_id: IDEA, x: 590, y: 454, width: 220, height: 92 })
  })

  it('selects all cards of an item for a deep link', async () => {
    const { api } = await mountCanvas({}, { focus: `item:${IDEA}` })
    await nextTick()
    expect(api.selection).toEqual([C1, C2])
  })
})

describe('MapCanvas with read access', () => {
  it('shows no drawing tools and writes nothing', async () => {
    const { api, source, wrapper: w } = await mountCanvas({ access: 'read' })
    expect(w.findAll('[data-tool]').map((b) => b.attributes('data-tool'))).toEqual(['select', 'hand'])
    expect(w.find('[aria-label="Undo"]').exists()).toBe(false)
    await drag(w, `[data-element-id="${P1}"]`, [810, 10], [900, 100])
    expect(row(source, P1)).toMatchObject({ x: 800, y: 0 })
    // Selecting still works: the side panel opens.
    expect(api.selection).toEqual([P1])
    await w.find('[data-testid="map-canvas"]').trigger('keydown', { key: 'Delete' })
    expect(row(source, P1)).toBeDefined()
    expect(source.log.value).toHaveLength(0)
  })
})
