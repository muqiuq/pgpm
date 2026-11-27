/**
 * Map geometry (docs/03 §13.2, §13.4), from PNMTD's components/graph/geometry.ts (edge paths, bounds) and extended for
 * PGPM's editor: where a connector meets a shape's border (rect, rounded rect, ellipse, diamond), whether a point is
 * inside a shape, the box around a set of elements, rubber-band selection and the grid. Pure functions, no framework.
 *
 * Coordinates are the map's world units; an element's frame is its top-left corner (`map_elements.x`, `y`) and its size.
 */

export interface Point {
  x: number
  y: number
}

export interface Box {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

/** An element's frame: top-left corner and size (> 0). */
export interface Rect {
  x: number
  y: number
  width: number
  height: number
}

export type ShapeKind = 'rect' | 'roundedRect' | 'ellipse' | 'diamond'

/** A frame drawn as a shape: a card or rectangle (`style.radius` makes it rounded), an ellipse, a gate's diamond. */
export interface Shape extends Rect {
  kind: ShapeKind
  /** roundedRect: the corner radius, at most half the shorter side. */
  radius?: number
}

/** How far the `index`-th of `count` edges between the same two nodes bends sideways: none when it is alone. */
export function bendOf(index: number, count: number, step = 18): number {
  return count > 1 ? (index - (count - 1) / 2) * step : 0
}

/** A straight line, or a quadratic curve bent sideways by `bend` units at its middle. */
export function edgePath(a: Point, b: Point, bend = 0): string {
  if (!bend) return `M ${a.x} ${a.y} L ${b.x} ${b.y}`
  const mx = (a.x + b.x) / 2
  const my = (a.y + b.y) / 2
  const dx = b.x - a.x
  const dy = b.y - a.y
  const d = Math.max(1, Math.hypot(dx, dy))
  return `M ${a.x} ${a.y} Q ${mx - (dy / d) * bend} ${my + (dx / d) * bend} ${b.x} ${b.y}`
}

/** The box around circles, or `fallback` when there are none. */
export function boundsOfCircles(circles: Iterable<{ x: number; y: number; r: number }>, fallback: Box): Box {
  const b = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity }
  for (const c of circles) {
    b.minX = Math.min(b.minX, c.x - c.r)
    b.minY = Math.min(b.minY, c.y - c.r)
    b.maxX = Math.max(b.maxX, c.x + c.r)
    b.maxY = Math.max(b.maxY, c.y + c.r)
  }
  return Number.isFinite(b.minX) ? b : fallback
}

/** The box around a set of frames (and points: a frame of size 0), or `fallback` when there are none. */
export function boundsOf(rects: Iterable<Rect | Point>, fallback: Box): Box {
  const b = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity }
  for (const r of rects) {
    const width = 'width' in r ? r.width : 0
    const height = 'height' in r ? r.height : 0
    b.minX = Math.min(b.minX, r.x)
    b.minY = Math.min(b.minY, r.y)
    b.maxX = Math.max(b.maxX, r.x + width)
    b.maxY = Math.max(b.maxY, r.y + height)
  }
  return Number.isFinite(b.minX) ? b : fallback
}

export function boxOf(r: Rect): Box {
  return { minX: r.x, minY: r.y, maxX: r.x + r.width, maxY: r.y + r.height }
}

export function rectOf(b: Box): Rect {
  return { x: b.minX, y: b.minY, width: b.maxX - b.minX, height: b.maxY - b.minY }
}

export function centreOf(r: Rect): Point {
  return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
}

/** The box grown by `padding` on every side (shrunk when negative). */
export function padBox(b: Box, padding: number): Box {
  return { minX: b.minX - padding, minY: b.minY - padding, maxX: b.maxX + padding, maxY: b.maxY + padding }
}

/** Two boxes share area (touching edges don't), or come closer than `gap`. */
export function boxesOverlap(a: Box, b: Box, gap = 0): boolean {
  return a.minX < b.maxX + gap && b.minX < a.maxX + gap && a.minY < b.maxY + gap && b.minY < a.maxY + gap
}

function radiusOf(s: Shape): number {
  return Math.max(0, Math.min(s.radius ?? 0, s.width / 2, s.height / 2))
}

/**
 * Where the ray from the shape's centre towards `toward` leaves the shape: a connector's end on its border. `toward`
 * at the centre: the centre.
 */
export function borderPoint(s: Shape, toward: Point): Point {
  const c = centreOf(s)
  const dx = toward.x - c.x
  const dy = toward.y - c.y
  if (dx === 0 && dy === 0) return c
  const hw = s.width / 2
  const hh = s.height / 2
  let t: number
  switch (s.kind) {
    case 'ellipse':
      t = 1 / Math.hypot(dx / hw, dy / hh)
      break
    case 'diamond':
      t = 1 / (Math.abs(dx) / hw + Math.abs(dy) / hh)
      break
    default: {
      t = Math.min(dx ? hw / Math.abs(dx) : Infinity, dy ? hh / Math.abs(dy) : Infinity)
      const r = s.kind === 'roundedRect' ? radiusOf(s) : 0
      // In a corner's square the border is the corner's arc: where the ray leaves that circle.
      if (r > 0 && Math.abs(t * dx) > hw - r && Math.abs(t * dy) > hh - r) {
        const kx = Math.sign(dx) * (hw - r)
        const ky = Math.sign(dy) * (hh - r)
        const a = dx * dx + dy * dy
        const b = -2 * (dx * kx + dy * ky)
        const k = kx * kx + ky * ky - r * r
        t = (-b + Math.sqrt(Math.max(0, b * b - 4 * a * k))) / (2 * a)
      }
    }
  }
  return { x: c.x + t * dx, y: c.y + t * dy }
}

/**
 * A connector's two ends (docs/03 §13.4): on the borders of the shapes it connects, facing each other along the line
 * between their centres; a free end is its point.
 */
export function connectorEnds(start: Shape | Point, end: Shape | Point): { start: Point; end: Point } {
  const isShape = (p: Shape | Point): p is Shape => 'kind' in p
  const a = isShape(start) ? centreOf(start) : start
  const b = isShape(end) ? centreOf(end) : end
  return {
    start: isShape(start) ? borderPoint(start, b) : start,
    end: isShape(end) ? borderPoint(end, a) : end,
  }
}

/** The point is inside the shape or on its border, or at most `tolerance` units outside it. */
export function containsPoint(s: Shape, p: Point, tolerance = 0): boolean {
  const c = centreOf(s)
  const qx = Math.abs(p.x - c.x)
  const qy = Math.abs(p.y - c.y)
  const hw = s.width / 2 + tolerance
  const hh = s.height / 2 + tolerance
  switch (s.kind) {
    case 'ellipse':
      return (qx / hw) ** 2 + (qy / hh) ** 2 <= 1
    case 'diamond':
      return qx / hw + qy / hh <= 1
    default: {
      if (qx > hw || qy > hh) return false
      const r = s.kind === 'roundedRect' ? radiusOf(s) + tolerance : 0
      if (r <= 0 || qx <= hw - r || qy <= hh - r) return true
      return Math.hypot(qx - (hw - r), qy - (hh - r)) <= r
    }
  }
}

/** The rubber band's box from where the drag began to where it is. */
export function bandBox(from: Point, to: Point): Box {
  return {
    minX: Math.min(from.x, to.x),
    minY: Math.min(from.y, to.y),
    maxX: Math.max(from.x, to.x),
    maxY: Math.max(from.y, to.y),
  }
}

/**
 * Rubber-band selection: `contain` takes frames wholly inside the band, `touch` those it touches. A drag to the right
 * contains, one to the left touches (as in drawing programs): `bandMode`.
 */
export type BandMode = 'contain' | 'touch'

export function bandMode(from: Point, to: Point): BandMode {
  return to.x >= from.x ? 'contain' : 'touch'
}

export function hitsBand(band: Box, r: Rect, mode: BandMode): boolean {
  const b = boxOf(r)
  if (mode === 'contain')
    return b.minX >= band.minX && b.maxX <= band.maxX && b.minY >= band.minY && b.maxY <= band.maxY
  return b.minX <= band.maxX && band.minX <= b.maxX && b.minY <= band.maxY && band.minY <= b.maxY
}

/** The ids of the frames the band selects, in their order. */
export function selectInBand<T extends Rect & { id: string }>(band: Box, rects: Iterable<T>, mode: BandMode): string[] {
  const ids: string[] = []
  for (const r of rects) if (hitsBand(band, r, mode)) ids.push(r.id)
  return ids
}

/** The nearest grid line (`grid` ≤ 0: no grid). */
export function snap(value: number, grid: number): number {
  return grid > 0 ? Math.round(value / grid) * grid + 0 : value
}

export function snapPoint(p: Point, grid: number): Point {
  return { x: snap(p.x, grid), y: snap(p.y, grid) }
}

/** A frame with all four edges on the grid, at least one cell in each direction (resizing with snap). */
export function snapRect(r: Rect, grid: number): Rect {
  if (grid <= 0) return r
  const x = snap(r.x, grid)
  const y = snap(r.y, grid)
  return {
    x,
    y,
    width: Math.max(grid, snap(r.x + r.width, grid) - x),
    height: Math.max(grid, snap(r.y + r.height, grid) - y),
  }
}
