import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import {
  centreOn,
  fitTo,
  MAX_ZOOM,
  MIN_ZOOM,
  toScreen,
  toWorld,
  usePanZoom,
  visibleBox,
  wheelFactor,
  zoomAround,
} from './panZoom'

describe('pan and zoom (docs/03 §13.2, from PNMTD)', () => {
  it('zooms around the pointer: the point under it stays', () => {
    const view = { x: 100, y: 50, k: 1 }
    const at = { x: 300, y: 200 }
    const before = toWorld(view, at)
    const zoomed = zoomAround(view, at, 2)
    expect(zoomed.k).toBe(2)
    expect(toWorld(zoomed, at)).toEqual(before)
  })

  it('stays within its limits', () => {
    expect(zoomAround({ x: 0, y: 0, k: 1 }, { x: 0, y: 0 }, 100).k).toBe(MAX_ZOOM)
    expect(zoomAround({ x: 0, y: 0, k: 1 }, { x: 0, y: 0 }, 0.0001).k).toBe(MIN_ZOOM)
  })

  it('zooms by the wheel gently, a notch by about 20 %, and caps big jumps', () => {
    expect(wheelFactor(0)).toBe(1)
    expect(wheelFactor(-60)).toBeCloseTo(1.197, 3)
    expect(wheelFactor(60)).toBeCloseTo(0.835, 3)
    expect(wheelFactor(1000)).toBe(wheelFactor(60))
    expect(wheelFactor(-4)).toBeGreaterThan(1)
  })

  it('fits a box into the viewport, centred', () => {
    const view = fitTo({ minX: -500, minY: -100, maxX: 500, maxY: 100 }, 1064, 600)
    expect(view.k).toBeCloseTo(1)
    expect(toWorld(view, { x: 532, y: 300 })).toEqual({ x: 0, y: 0 })
    expect(fitTo({ minX: 0, minY: 0, maxX: 10, maxY: 10 }, 800, 600).k).toBe(1.5)
    expect(fitTo({ minX: 0, minY: 0, maxX: 1e6, maxY: 10 }, 800, 600).k).toBe(MIN_ZOOM)
  })

  it('centres a point', () => {
    const view = centreOn({ x: 0, y: 0, k: 2 }, { x: 40, y: -20 }, 800, 600)
    expect(toWorld(view, { x: 400, y: 300 })).toEqual({ x: 40, y: -20 })
    expect(centreOn({ x: 0, y: 0, k: 0.5 }, { x: 0, y: 0 }, 800, 600).k).toBe(1)
  })

  it('converts both ways and knows the visible part of the map', () => {
    const view = { x: 100, y: -40, k: 2 }
    expect(toScreen(view, toWorld(view, { x: 13, y: 17 }))).toEqual({ x: 13, y: 17 })
    expect(visibleBox(view, 800, 600)).toEqual({ minX: -50, minY: 20, maxX: 350, maxY: 320 })
  })
})

describe('usePanZoom', () => {
  function setup() {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg')
    svg.getBoundingClientRect = () => ({ left: 10, top: 20, width: 800, height: 600 }) as DOMRect
    const panZoom = usePanZoom(ref(svg))
    const pointer = (id: number, x: number, y: number) =>
      ({ pointerId: id, clientX: x + 10, clientY: y + 20, currentTarget: svg }) as unknown as PointerEvent
    return { panZoom, pointer }
  }

  it('pans by dragging the background', () => {
    const { panZoom, pointer } = setup()
    panZoom.onPointerDown(pointer(1, 100, 100))
    panZoom.onPointerMove(pointer(1, 130, 90))
    panZoom.onPointerUp(pointer(1, 130, 90))
    panZoom.onPointerMove(pointer(1, 500, 500))
    expect(panZoom.view.value).toEqual({ x: 30, y: -10, k: 1 })
  })

  it('zooms with two pointers around their middle', () => {
    const { panZoom, pointer } = setup()
    panZoom.onPointerDown(pointer(1, 300, 300))
    panZoom.onPointerDown(pointer(2, 500, 300))
    panZoom.onPointerMove(pointer(2, 700, 300))
    expect(panZoom.view.value.k).toBeCloseTo(2)
    panZoom.onPointerUp(pointer(2, 700, 300))
    panZoom.onPointerUp(pointer(1, 300, 300))
  })

  it('zooms by the wheel and the buttons, fits and centres, and maps pointers to the world', () => {
    const { panZoom, pointer } = setup()
    const wheel = { deltaY: -60, clientX: 410, clientY: 320, preventDefault: () => undefined } as WheelEvent
    panZoom.onWheel(wheel)
    expect(panZoom.view.value.k).toBeCloseTo(1.197, 3)
    panZoom.zoomBy(0.8)
    expect(panZoom.view.value.k).toBeCloseTo(0.958, 3)
    panZoom.fit({ minX: 0, minY: 0, maxX: 100, maxY: 100 })
    expect(panZoom.view.value.k).toBe(1.5)
    panZoom.centre({ x: 0, y: 0 })
    expect(panZoom.toWorld(pointer(1, 400, 300))).toEqual({ x: 0, y: 0 })
    expect(panZoom.visible().maxX - panZoom.visible().minX).toBeCloseTo(800 / 1.5)
  })
})
