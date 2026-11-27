import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { mountWithUi } from '@/test/mount'
import { createMemorySource, type MemorySource } from './memorySource'
import { matrixCardId } from './matrixItems'
import MatrixMap from './MatrixMap.vue'
import { SETTLE_MS } from './culling'
import { fire, IDEA, MAP_ID, PROJECT, seed, uid } from './testFixtures'
import type { ItemRow } from './types'

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

const SECOND = uid('14')

async function mountMatrix(): Promise<{ source: MemorySource; w: VueWrapper }> {
  const second: ItemRow = { id: SECOND, type: 'idea', number: 8, title: 'Second idea' }
  const base = seed()
  const source = createMemorySource({
    ...base,
    map: {
      id: MAP_ID,
      kind: 'matrix',
      name: 'Matrix',
      access: 'edit',
      settings: { cardBadges: ['feasibility'] },
      matrix: {
        x: { field: 'feasibility', label: 'Ease', min: 0, max: 10 },
        y: { field: 'impact', label: 'Impact', min: 0, max: 100 },
        quadrants: { tr: 'Stars' },
        filter: { types: ['idea', 'project'] },
      },
    },
    // Free elements only: the cards come from the items.
    elements: base.elements!.filter((e) => e.kind === 'text'),
    items: [...base.items!, second],
    fieldValues: [
      ...base.fieldValues!,
      { id: uid('v8'), item_id: SECOND, field_id: uid('f1'), value: 1 },
      { id: uid('v9'), item_id: SECOND, field_id: uid('f2'), value: 1 },
      { id: uid('va'), item_id: SECOND, field_id: uid('f4'), value: 20 },
    ],
  })
  const w = mountWithUi(MatrixMap, { props: { source }, attachTo: document.body })
  await flushPromises()
  // Close enough to read the cards.
  ;(w.vm as unknown as { canvas: { setView: (v: { x: number; y: number; k: number }) => void } }).canvas.setView({
    x: 0,
    y: 0,
    k: 1,
  })
  await new Promise((resolve) => setTimeout(resolve, SETTLE_MS + 20))
  await nextTick()
  return { source, w }
}

const card = (w: VueWrapper, itemId: string) => w.find(`[data-element-id="${matrixCardId(itemId)}"]`)
const position = (w: VueWrapper, itemId: string) => card(w, itemId).attributes('style')

describe('MatrixMap', () => {
  it('places assessed items as cards with their quadrant, the others in the tray', async () => {
    const { w } = await mountMatrix()
    wrapper = w
    expect(card(w, IDEA).exists()).toBe(true)
    expect(card(w, SECOND).exists()).toBe(true)
    expect(card(w, PROJECT).exists()).toBe(false)
    expect(card(w, IDEA).text()).toContain('Stars')
    expect(card(w, SECOND).text()).toContain('Limited interest')
    const tray = w.find('aside.tray')
    expect(tray.text()).toContain('Not yet assessed')
    expect(tray.find(`[data-tray-item="${PROJECT}"]`).exists()).toBe(true)
    // The axes and quadrant labels.
    expect(w.find('.matrix-background').text()).toContain('Ease')
    expect(w.find('.matrix-background').text()).toContain('Quick wins')
    // The free text on top.
    expect(w.text()).toContain('Hello')
  })

  it('moves a card, animated, when a value changes', async () => {
    const { source, w } = await mountMatrix()
    wrapper = w
    expect(card(w, IDEA).classes()).toContain('animated')
    const before = position(w, IDEA)
    source.put('field_values', { id: uid('v3'), item_id: IDEA, field_id: uid('f4'), value: 5 })
    await nextTick()
    expect(position(w, IDEA)).not.toBe(before)
    expect(card(w, IDEA).text()).toContain('Quick wins')
  })

  it('cards can be selected but not dragged', async () => {
    const { source, w } = await mountMatrix()
    wrapper = w
    const svg = w.find('svg.surface').element
    await fire(card(w, IDEA).element, 'pointerdown', 10, 10)
    await fire(svg, 'pointermove', 200, 200)
    await fire(svg, 'pointerup', 200, 200)
    expect(source.log.value).toHaveLength(0)
    expect(document.body.textContent).toContain('I-7 Chatbot')
  })

  it('opens an item of the tray in the side panel', async () => {
    const { w } = await mountMatrix()
    wrapper = w
    await w.find(`[data-tray-item="${PROJECT}"]`).trigger('click')
    await nextTick()
    expect(document.body.textContent).toContain('P-3 Website')
  })
})
