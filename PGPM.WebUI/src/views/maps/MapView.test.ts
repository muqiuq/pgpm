import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { useDatabase, type PgpmDb } from '@/local/db'
import { repo, setRepoContext } from '@/local/repo'
import { SETTLE_MS } from '@/maps/culling'
import MapCanvas from '@/maps/MapCanvas.vue'
import type { MemorySeed } from '@/maps/memorySource'
import { matrixCardId } from '@/maps/matrixItems'
import { IDEA, MAP_ID, NOTE, PROJECT, seed, uid } from '@/maps/testFixtures'
import type { ItemRequest } from '@/maps/tools'
import { storeMapSeed } from '@/test/mapSeed'
import { memoryDb, until } from '@/test/memoryDb'
import { button, mountPage } from '@/test/mountPage'
import { ITEM_DRAG_TYPE } from './mapList'
import MapView from './MapView.vue'

const { route, push } = vi.hoisted(() => ({ route: { query: {} as Record<string, string> }, push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push, replace: vi.fn() }) }))

describe('a map’s page (doc 01 §12, docs/03 §13)', () => {
  let db: PgpmDb
  let wrapper: VueWrapper | null = null

  beforeEach(() => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    route.query = {}
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    // jsdom draws no canvas: no preview is rendered when the page goes.
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null)
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    )
  })
  afterEach(async () => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  async function open(data: MemorySeed, rights: Record<string, string> = { Ideas: 'Create', Notes: 'Create' }) {
    await storeMapSeed(db, data)
    wrapper = mountPage(MapView, { id: MAP_ID }, { rights, groupIds: [] })
    await until(() => !!document.querySelector('[data-testid="map-canvas"]'), 'the canvas')
    await flushPromises()
    return wrapper
  }
  const canvasOf = (w: VueWrapper) =>
    w.findComponent(MapCanvas).vm as unknown as {
      setView: (v: { x: number; y: number; k: number }) => void
      toWorld: (x: number, y: number) => { x: number; y: number }
    }
  const cardsOf = async (itemId: string) =>
    (await db.synced('map_elements').where('itemId').equals(itemId).toArray()).filter((e) => !e.deleted)

  function drop(itemId: string, x: number, y: number) {
    const event = new MouseEvent('drop', { bubbles: true, cancelable: true, clientX: x, clientY: y })
    const data = { types: [ITEM_DRAG_TYPE], getData: (type: string) => (type === ITEM_DRAG_TYPE ? itemId : '') }
    Object.defineProperty(event, 'dataTransfer', { value: data })
    document.querySelector('[data-testid="map-stage"]')!.dispatchEvent(event)
  }

  it('shows a matrix map’s assessed items in their quadrants; a card moves when a rating changes', async () => {
    const base = seed()
    const w = await open({
      ...base,
      map: {
        id: MAP_ID,
        kind: 'matrix',
        name: 'Prioritization',
        access: 'edit',
        settings: { cardBadges: ['feasibility'] },
        matrix: {
          x: { field: 'feasibility', label: 'Ease', min: 0, max: 10 },
          y: { field: 'impact', label: 'Impact', min: 0, max: 100 },
          quadrants: { tr: 'Stars' },
          filter: { types: ['idea', 'project'] },
        },
      },
      elements: base.elements!.filter((e) => e.kind === 'text'),
    })
    expect(w.text()).toContain('Prioritization')
    expect(button('Library')).toBeUndefined()
    const card = () => document.querySelector(`[data-element-id="${matrixCardId(IDEA)}"]`)
    await until(() => !!card(), 'the idea’s card')
    canvasOf(w).setView({ x: 0, y: 0, k: 1 })
    await new Promise((r) => setTimeout(r, SETTLE_MS + 20))
    await nextTick()
    expect(card()!.textContent).toContain('Stars')
    expect(document.querySelector('aside.tray')!.textContent).toContain('Website')
    const before = card()!.getAttribute('style')
    // A rating changes on this device (or arrives through the sync): the card moves into another quadrant.
    await repo.patch('field_values', uid('v3'), { value: 5 })
    await until(() => card()!.getAttribute('style') !== before, 'the move')
    await until(() => card()!.textContent!.includes('Quick wins'), 'the new quadrant')
  })

  it('drops an item from the library onto the canvas, and adds one in the middle', async () => {
    const w = await open(seed())
    expect(await cardsOf(NOTE)).toHaveLength(0)
    button('Library')!.click()
    await until(() => !!document.querySelector(`[data-testid="library"] [data-item="${NOTE}"]`), 'the library')
    expect(document.querySelector(`[data-testid="library"] [data-item="${IDEA}"]`)!.textContent).toContain(
      '2× on the map',
    )
    canvasOf(w).setView({ x: 0, y: 0, k: 1 })
    drop(NOTE, 300, 200)
    await until(async () => (await cardsOf(NOTE)).length === 1, 'the dropped card')
    const [card] = await cardsOf(NOTE)
    const at = canvasOf(w).toWorld(300, 200)
    expect(card!.x).toBeCloseTo(at.x - 110, 1)
    expect(card!.y).toBeCloseTo(at.y - 46, 1)
    button('Add Workshop to the map')!.click()
    await until(async () => (await cardsOf(NOTE)).length === 2, 'the added card')
  })

  it('answers an item tool’s request: Create new with the dialog, its card where the user clicked', async () => {
    const w = await open(seed())
    const place = vi.fn()
    const request: ItemRequest = {
      at: { x: 10, y: 10 },
      place,
      cancel: vi.fn(),
      onMap: new Set([IDEA]),
      extra: { type: 'note' },
    }
    w.findComponent(MapCanvas).vm.$emit('requestItem', request)
    await until(() => !!document.querySelector('#place-title'), 'the dialog')
    const existing = document.querySelector(`[data-testid="place-dialog"] [data-item="${NOTE}"]`)
    expect(existing).not.toBeNull()
    const input = document.querySelector<HTMLInputElement>('#place-title')!
    input.value = 'Fresh note'
    input.dispatchEvent(new Event('input'))
    button('Create note')!.click()
    await until(() => place.mock.calls.length === 1, 'the placement')
    const item = await db.synced('items').get(place.mock.calls[0]![0] as string)
    expect(item).toMatchObject({ type: 'note', title: 'Fresh note' })
  })

  it('opens the item of ?focus=item:<id> in the side panel with its quick edit', async () => {
    route.query = { focus: `item:${PROJECT}` }
    await open(seed())
    await until(() => !!document.querySelector('[data-testid="item-quick-edit"]'), 'the quick edit')
    expect(document.querySelector('[data-testid="item-quick-edit"]')!.textContent).toContain('Website')
    button('Open details')!.click()
    expect(push).toHaveBeenCalledWith({ name: 'project', params: { id: PROJECT } })
  })

  it('is read-only with read access: no tools, nothing added by the library', async () => {
    const w = await open(seed({ access: 'read' }))
    expect(w.text()).toContain('Read only')
    button('Library')!.click()
    await until(() => !!document.querySelector(`[data-testid="library"] [data-item="${NOTE}"]`), 'the library')
    expect(button('Add Workshop to the map')).toBeUndefined()
    drop(NOTE, 300, 200)
    await new Promise((r) => setTimeout(r, 30))
    expect(await cardsOf(NOTE)).toHaveLength(0)
    expect(document.querySelector('[aria-label="Rectangle"]')).toBeNull()
  })

  it('says so when the map isn’t on the device', async () => {
    wrapper = mountPage(MapView, { id: 'nowhere' }, {})
    await until(() => wrapper!.text().includes('This map is not on this device.'), 'the empty state')
  })
})
