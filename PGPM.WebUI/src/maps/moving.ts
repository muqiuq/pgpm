import { getCurrentInstance, onBeforeUnmount, type Ref } from 'vue'
import type { Point } from './geometry'

/**
 * Moving elements on a map by hand (docs/03 §13.2, §13.4), PNMTD's components/graph/useMoving.ts: a drag moves one
 * element or the whole selection, a focused selection moves with the arrow keys (8 units, 40 with Shift), and Escape
 * during a drag puts it back. During a drag only `moved` changes (Vue state); the caller writes one patch per element
 * when `release()` reports the end of a drag. With `snap`, the first dragged element's position snaps to the grid and
 * the others keep their distance to it.
 */
export interface MovingOptions {
  /** Where an element is now: its move, or its stored place. */
  at: (id: string) => Point
  /** The map's point under the pointer. */
  toWorld: (e: PointerEvent) => Point
  /** Snap to the grid (geometry's snapPoint), when the map snaps. */
  snap?: (p: Point) => Point
}

/** A move counts once the pointer went further than this (in world units, |dx| + |dy|): less is a click. */
export const DRAG_THRESHOLD = 2

const arrows: Record<string, Point> = {
  ArrowLeft: { x: -1, y: 0 },
  ArrowRight: { x: 1, y: 0 },
  ArrowUp: { x: 0, y: -1 },
  ArrowDown: { x: 0, y: 1 },
}

export function useMoving(moved: Ref<Map<string, Point>>, options: MovingOptions) {
  function place(points: Map<string, Point>) {
    const next = new Map(moved.value)
    for (const [id, p] of points) next.set(id, p)
    moved.value = next
  }

  /** A drag: the elements it moves, where they were, and the moves they had before (Escape puts those back). */
  let dragging: {
    ids: string[]
    from: Map<string, Point>
    before: Map<string, Point | undefined>
    start: Point
  } | null = null
  let dragMoved = false

  function begin(e: PointerEvent, ids: string[]) {
    e.stopPropagation()
    try {
      ;(e.currentTarget as Element).setPointerCapture(e.pointerId)
    } catch {
      // A pointer the browser doesn't track (a synthetic event): the drag works without capture.
    }
    dragging = {
      ids,
      from: new Map(ids.map((id) => [id, options.at(id)])),
      before: new Map(ids.map((id) => [id, moved.value.get(id)])),
      start: options.toWorld(e),
    }
    dragMoved = false
    window.addEventListener('keydown', onEscape)
  }

  function move(e: PointerEvent) {
    if (!dragging) return
    const now = options.toWorld(e)
    let dx = now.x - dragging.start.x
    let dy = now.y - dragging.start.y
    if (Math.abs(dx) + Math.abs(dy) > DRAG_THRESHOLD) dragMoved = true
    if (!dragMoved) return
    const d = dragging
    const anchor = d.from.get(d.ids[0]!)
    if (options.snap && anchor) {
      const snapped = options.snap({ x: anchor.x + dx, y: anchor.y + dy })
      dx = snapped.x - anchor.x
      dy = snapped.y - anchor.y
    }
    place(new Map(d.ids.map((id) => [id, { x: d.from.get(id)!.x + dx, y: d.from.get(id)!.y + dy }])))
  }

  function end() {
    dragging = null
    window.removeEventListener('keydown', onEscape)
  }

  function onEscape(e: KeyboardEvent) {
    if (e.key !== 'Escape' || !dragging) return
    e.preventDefault()
    const next = new Map(moved.value)
    for (const [id, p] of dragging.before) {
      if (p) next.set(id, p)
      else next.delete(id)
    }
    moved.value = next
    end()
  }
  if (getCurrentInstance()) onBeforeUnmount(end)

  /**
   * A pointer went up on an element: `click` when it was a click, not a drag (a click selects); the ids a drag moved
   * otherwise (one patch each). Ends the drag.
   */
  function release(): { click: boolean; ids: string[] } {
    const click = dragging !== null && !dragMoved
    const ids = dragging && dragMoved ? dragging.ids : []
    end()
    return { click, ids }
  }

  /** The arrow keys move a focused element or the selection: 8 units, 40 with Shift. The ids moved. */
  function nudge(e: KeyboardEvent, ids: string[]): string[] {
    const d = arrows[e.key]
    if (!d || !ids.length) return []
    e.preventDefault()
    const step = e.shiftKey ? 40 : 8
    place(new Map(ids.map((id) => [id, { x: options.at(id).x + d.x * step, y: options.at(id).y + d.y * step }])))
    return ids
  }

  return { place, begin, move, end, release, nudge, isDragging: () => dragging !== null }
}
