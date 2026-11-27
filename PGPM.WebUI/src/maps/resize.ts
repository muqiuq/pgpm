import { snap, type Point, type Rect } from './geometry'

/**
 * Resizing an element by its handles (doc 01 §12.2, docs/03 §13.2): the handle's edges follow the pointer, the others
 * stay; Shift keeps the aspect ratio (a corner scales from the opposite corner, an edge from the middle of the other
 * axis); with snap, the moving edges land on the grid. An element never gets smaller than MIN_SIZE and never flips.
 */

export type Handle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w'
export const HANDLES: readonly Handle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']
export const MIN_SIZE = 8

/** Where a handle sits on the frame. */
export function handlePoint(frame: Rect, handle: Handle): Point {
  const x = handle.includes('w') ? frame.x : handle.includes('e') ? frame.x + frame.width : frame.x + frame.width / 2
  const y = handle.includes('n') ? frame.y : handle.includes('s') ? frame.y + frame.height : frame.y + frame.height / 2
  return { x, y }
}

export const HANDLE_CURSORS: Readonly<Record<Handle, string>> = {
  nw: 'nwse-resize',
  se: 'nwse-resize',
  ne: 'nesw-resize',
  sw: 'nesw-resize',
  n: 'ns-resize',
  s: 'ns-resize',
  e: 'ew-resize',
  w: 'ew-resize',
}

export interface ResizeOptions {
  keepRatio?: boolean
  /** Grid size to snap to; 0: none. */
  grid?: number
  min?: number
}

export function resizeFrame(start: Rect, handle: Handle, to: Point, options: ResizeOptions = {}): Rect {
  const min = options.min ?? MIN_SIZE
  const grid = options.grid ?? 0
  const west = handle.includes('w')
  const east = handle.includes('e')
  const north = handle.includes('n')
  const south = handle.includes('s')
  let left = start.x
  let right = start.x + start.width
  let top = start.y
  let bottom = start.y + start.height
  if (west) left = Math.min(snap(to.x, grid), right - min)
  if (east) right = Math.max(snap(to.x, grid), left + min)
  if (north) top = Math.min(snap(to.y, grid), bottom - min)
  if (south) bottom = Math.max(snap(to.y, grid), top + min)
  let width = right - left
  let height = bottom - top
  if (!options.keepRatio || start.width <= 0 || start.height <= 0) return { x: left, y: top, width, height }

  const ratio = start.width / start.height
  const corner = (west || east) && (north || south)
  if (corner) {
    const scale = Math.max(width / start.width, height / start.height)
    width = Math.max(min, start.width * scale)
    height = width / ratio
    if (height < min) {
      height = min
      width = height * ratio
    }
    return {
      x: west ? start.x + start.width - width : start.x,
      y: north ? start.y + start.height - height : start.y,
      width,
      height,
    }
  }
  if (west || east) {
    height = Math.max(min, width / ratio)
    width = height * ratio
    return {
      x: west ? start.x + start.width - width : start.x,
      y: start.y + (start.height - height) / 2,
      width,
      height,
    }
  }
  width = Math.max(min, height * ratio)
  height = width / ratio
  return {
    x: start.x + (start.width - width) / 2,
    y: north ? start.y + start.height - height : start.y,
    width,
    height,
  }
}
