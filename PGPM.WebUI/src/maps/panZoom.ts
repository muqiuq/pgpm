import { ref, type Ref } from 'vue'
import type { Box, Point } from './geometry'

/**
 * Pan and zoom for a map's SVG (docs/03 §13.2), PNMTD's composables/usePanZoom.ts: the wheel and pinch zoom around the
 * pointer, dragging the background pans, and buttons zoom around the middle or fit the whole drawing. The maths is in
 * pure functions with their tests; the composable only holds the view and the pointers.
 */

/** The world group's transform: `translate(x y) scale(k)`. */
export interface View {
  x: number
  y: number
  k: number
}

export const MIN_ZOOM = 0.1
export const MAX_ZOOM = 4

/** The view zoomed by `factor`, keeping the screen point `at` where it is. */
export function zoomAround(view: View, at: Point, factor: number, min = MIN_ZOOM, max = MAX_ZOOM): View {
  const k = Math.min(max, Math.max(min, view.k * factor))
  const f = k / view.k
  return { k, x: at.x - (at.x - view.x) * f, y: at.y - (at.y - view.y) * f }
}

/** A wheel step's zoom factor: a trackpad's small steps zoom gently, a mouse wheel's notch by about 20 %. */
export function wheelFactor(deltaY: number): number {
  return Math.exp(-Math.max(-60, Math.min(60, deltaY)) * 0.003)
}

/** The view that shows the whole box in a viewport of width × height, with padding, at most at zoom 1.5. */
export function fitTo(box: Box, width: number, height: number, padding = 32, max = 1.5): View {
  const w = Math.max(1, box.maxX - box.minX)
  const h = Math.max(1, box.maxY - box.minY)
  const k = Math.max(MIN_ZOOM, Math.min(max, (width - 2 * padding) / w, (height - 2 * padding) / h))
  return { k, x: width / 2 - (box.minX + w / 2) * k, y: height / 2 - (box.minY + h / 2) * k }
}

/** A screen point in the map's coordinates. */
export function toWorld(view: View, at: Point): Point {
  return { x: (at.x - view.x) / view.k, y: (at.y - view.y) / view.k }
}

/** A map point on the screen. */
export function toScreen(view: View, point: Point): Point {
  return { x: point.x * view.k + view.x, y: point.y * view.k + view.y }
}

/** The view centred on a map point, at the zoom it has (at least 1). */
export function centreOn(view: View, point: Point, width: number, height: number): View {
  const k = Math.max(1, view.k)
  return { k, x: width / 2 - point.x * k, y: height / 2 - point.y * k }
}

/** The part of the map the viewport shows (viewport culling, the minimap's frame). */
export function visibleBox(view: View, width: number, height: number): Box {
  const a = toWorld(view, { x: 0, y: 0 })
  const b = toWorld(view, { x: width, y: height })
  return { minX: a.x, minY: a.y, maxX: b.x, maxY: b.y }
}

export function usePanZoom(svg: Ref<SVGSVGElement | null>) {
  const view = ref<View>({ x: 0, y: 0, k: 1 })
  const pointers = new Map<number, Point>()
  let pinch: { distance: number; view: View } | null = null
  let panning = false

  function local(e: { clientX: number; clientY: number }): Point {
    const rect = svg.value?.getBoundingClientRect()
    return { x: e.clientX - (rect?.left ?? 0), y: e.clientY - (rect?.top ?? 0) }
  }

  function size() {
    const rect = svg.value?.getBoundingClientRect()
    return { width: rect?.width ?? 800, height: rect?.height ?? 600 }
  }

  function onWheel(e: WheelEvent) {
    e.preventDefault()
    view.value = zoomAround(view.value, local(e), wheelFactor(e.deltaY))
  }

  function twoPointers(): [Point, Point] {
    return [...pointers.values()] as [Point, Point]
  }

  /** On the background only: an element's own pointerdown stops it, to drag the element instead. */
  function onPointerDown(e: PointerEvent) {
    pointers.set(e.pointerId, local(e))
    try {
      ;(e.currentTarget as Element | null)?.setPointerCapture?.(e.pointerId)
    } catch {
      // A pointer the browser doesn't track: panning works without capture.
    }
    if (pointers.size === 2) {
      const [a, b] = twoPointers()
      pinch = { distance: Math.hypot(a.x - b.x, a.y - b.y), view: view.value }
      panning = false
    } else {
      panning = true
    }
  }

  function onPointerMove(e: PointerEvent) {
    const before = pointers.get(e.pointerId)
    if (!before) return
    const now = local(e)
    pointers.set(e.pointerId, now)
    if (pinch && pointers.size === 2) {
      const [a, b] = twoPointers()
      const distance = Math.hypot(a.x - b.x, a.y - b.y)
      view.value = zoomAround(
        pinch.view,
        { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 },
        distance / Math.max(1, pinch.distance),
      )
    } else if (panning) {
      view.value = { ...view.value, x: view.value.x + now.x - before.x, y: view.value.y + now.y - before.y }
    }
  }

  function onPointerUp(e: PointerEvent) {
    pointers.delete(e.pointerId)
    if (pointers.size < 2) pinch = null
    if (pointers.size === 0) panning = false
  }

  /** The +/− buttons: 1.25 and 0.8 around the middle. */
  function zoomBy(factor: number) {
    const { width, height } = size()
    view.value = zoomAround(view.value, { x: width / 2, y: height / 2 }, factor)
  }

  function fit(box: Box) {
    const { width, height } = size()
    view.value = fitTo(box, width, height)
  }

  function centre(point: Point) {
    const { width, height } = size()
    view.value = centreOn(view.value, point, width, height)
  }

  return {
    view,
    onWheel,
    onPointerDown,
    onPointerMove,
    onPointerUp,
    zoomBy,
    fit,
    centre,
    toWorld: (e: { clientX: number; clientY: number }) => toWorld(view.value, local(e)),
    visible: () => {
      const { width, height } = size()
      return visibleBox(view.value, width, height)
    },
  }
}
