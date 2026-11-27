import {
  borderPoint,
  centreOf,
  connectorEnds,
  containsPoint,
  type Box,
  type Point,
  type Rect,
  type Shape,
} from './geometry'
import type { ElementRow, ElementStyle, MilestoneRow } from './types'

/**
 * The drawing's geometry (docs/03 §13.1, §13.4) on top of geometry.ts: an element's frame and shape, a line's path with
 * its connected ends following the elements they are attached to, arrow heads, the box an element covers (culling,
 * rubber band, fit), hit tests, the timeline's marks. Pure functions.
 */

/** Map coordinates are stored with two decimals (data model §9). */
export function round2(value: number): number {
  return Math.round(value * 100) / 100 + 0
}

export function frameOf(el: Pick<ElementRow, 'x' | 'y' | 'width' | 'height'>): Rect {
  return { x: el.x, y: el.y, width: el.width, height: el.height }
}

/** Corner radius of item cards. */
export const CARD_RADIUS = 8

/** The outline connectors meet and clicks hit: cards are rounded, gates diamonds, milestones and ellipses round. */
export function shapeOf(el: Pick<ElementRow, 'kind' | 'style'>, frame: Rect, milestone?: MilestoneRow): Shape {
  switch (el.kind) {
    case 'item':
      return { ...frame, kind: 'roundedRect', radius: CARD_RADIUS }
    case 'ellipse':
      return { ...frame, kind: 'ellipse' }
    case 'rect': {
      const radius = el.style?.radius ?? 0
      return radius > 0 ? { ...frame, kind: 'roundedRect', radius } : { ...frame, kind: 'rect' }
    }
    case 'milestone':
      return { ...frame, kind: milestone?.kind === 'gate' ? 'diamond' : 'ellipse' }
    default:
      return { ...frame, kind: 'rect' }
  }
}

/** A line's two stored ends, absolute: `points` relative to x, y (a missing one: the frame's corners). */
export function storedEnds(el: Pick<ElementRow, 'x' | 'y' | 'width' | 'height' | 'points'>): [Point, Point] {
  const p = el.points
  const a = p?.[0] ?? [0, 0]
  const b = p?.[1] ?? [el.width, el.height]
  return [
    { x: el.x + a[0], y: el.y + a[1] },
    { x: el.x + b[0], y: el.y + b[1] },
  ]
}

/** The stored form of a line between two points: its frame is the points' box (at least 1 × 1), points relative. */
export function lineRow(a: Point, b: Point): Pick<ElementRow, 'x' | 'y' | 'width' | 'height' | 'points'> {
  const x = round2(Math.min(a.x, b.x))
  const y = round2(Math.min(a.y, b.y))
  return {
    x,
    y,
    width: Math.max(1, round2(Math.abs(a.x - b.x))),
    height: Math.max(1, round2(Math.abs(a.y - b.y))),
    points: [
      [round2(a.x - x), round2(a.y - y)],
      [round2(b.x - x), round2(b.y - y)],
    ],
  }
}

export interface ArrowHeadPath {
  kind: 'arrow' | 'triangle' | 'dot'
  d: string
}

export interface LineGeometry {
  start: Point
  end: Point
  /** The line's path (a triangle head shortens it a little, so that the stroke doesn't poke through the tip). */
  d: string
  heads: ArrowHeadPath[]
  /** Where the label sits. */
  mid: Point
  box: Box
}

function unit(from: Point, to: Point): Point {
  const dx = to.x - from.x
  const dy = to.y - from.y
  const d = Math.hypot(dx, dy)
  return d > 0 ? { x: dx / d, y: dy / d } : { x: 1, y: 0 }
}

const fmt = (n: number) => round2(n)

/** An arrow head at `tip`, pointing along `dir` (a unit vector), for a stroke of `width`. */
export function arrowHead(kind: 'arrow' | 'triangle' | 'dot', tip: Point, dir: Point, width: number): ArrowHeadPath {
  const size = Math.max(8, width * 3.5)
  if (kind === 'dot') {
    const r = size / 2.6
    return {
      kind,
      d: `M ${fmt(tip.x - r)} ${fmt(tip.y)} a ${fmt(r)} ${fmt(r)} 0 1 0 ${fmt(2 * r)} 0 a ${fmt(r)} ${fmt(r)} 0 1 0 ${fmt(-2 * r)} 0 Z`,
    }
  }
  const back = { x: tip.x - dir.x * size, y: tip.y - dir.y * size }
  const half = size * (kind === 'triangle' ? 0.5 : 0.55)
  const left = { x: back.x - dir.y * half, y: back.y + dir.x * half }
  const right = { x: back.x + dir.y * half, y: back.y - dir.x * half }
  const d = `M ${fmt(left.x)} ${fmt(left.y)} L ${fmt(tip.x)} ${fmt(tip.y)} L ${fmt(right.x)} ${fmt(right.y)}`
  return { kind, d: kind === 'triangle' ? `${d} Z` : d }
}

/** An end of a line: a free point, or the shape of the element it is connected to. */
export type LineEnd = Point | Shape

const isShape = (p: LineEnd): p is Shape => 'kind' in p

/**
 * A line's geometry: straight, or elbow (horizontal–vertical–horizontal when it runs more across than down, else
 * vertical–horizontal–vertical). Connected straight ends sit on the shapes' borders facing each other
 * (geometry.connectorEnds); elbow ends leave from the middle of the facing sides.
 */
export function lineGeometry(start: LineEnd, end: LineEnd, style: ElementStyle | null | undefined): LineGeometry {
  const width = style?.strokeWidth ?? 2
  const elbow = style?.lineType === 'elbow'
  let a: Point
  let b: Point
  const ca = isShape(start) ? centreOf(start) : start
  const cb = isShape(end) ? centreOf(end) : end
  const across = Math.abs(cb.x - ca.x) >= Math.abs(cb.y - ca.y)
  if (elbow) {
    const sx = Math.sign(cb.x - ca.x) || 1
    const sy = Math.sign(cb.y - ca.y) || 1
    a = isShape(start) ? borderPoint(start, across ? { x: ca.x + sx, y: ca.y } : { x: ca.x, y: ca.y + sy }) : start
    b = isShape(end) ? borderPoint(end, across ? { x: cb.x - sx, y: cb.y } : { x: cb.x, y: cb.y - sy }) : end
  } else {
    ;({ start: a, end: b } = connectorEnds(start, end))
  }
  // The segments: the first and last direction decide the heads.
  const points: Point[] = [a]
  if (elbow && a.x !== b.x && a.y !== b.y) {
    if (across) {
      const mx = (a.x + b.x) / 2
      points.push({ x: mx, y: a.y }, { x: mx, y: b.y })
    } else {
      const my = (a.y + b.y) / 2
      points.push({ x: a.x, y: my }, { x: b.x, y: my })
    }
  }
  points.push(b)
  const heads: ArrowHeadPath[] = []
  const drawn = points.map((p) => ({ ...p }))
  const headAt = (kind: ElementStyle['arrowEnd'], tip: Point, from: Point, index: number) => {
    if (!kind || kind === 'none') return
    const dir = unit(from, tip)
    heads.push(arrowHead(kind, tip, dir, width))
    if (kind === 'triangle') {
      const size = Math.max(8, width * 3.5) * 0.6
      drawn[index] = { x: tip.x - dir.x * size, y: tip.y - dir.y * size }
    }
  }
  headAt(style?.arrowEnd, b, points[points.length - 2]!, drawn.length - 1)
  headAt(style?.arrowStart, a, points[1]!, 0)
  const d = drawn.map((p, i) => `${i ? 'L' : 'M'} ${fmt(p.x)} ${fmt(p.y)}`).join(' ')
  const middle =
    points.length === 4
      ? { x: (points[1]!.x + points[2]!.x) / 2, y: (points[1]!.y + points[2]!.y) / 2 }
      : { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
  return {
    start: a,
    end: b,
    d,
    heads,
    mid: middle,
    box: {
      minX: Math.min(a.x, b.x),
      minY: Math.min(a.y, b.y),
      maxX: Math.max(a.x, b.x),
      maxY: Math.max(a.y, b.y),
    },
  }
}

/** The dash pattern of a stroke style, scaled with the width. */
export function dashArray(style: ElementStyle['strokeStyle'], width: number): string | undefined {
  if (style === 'dashed') return `${width * 4} ${width * 3}`
  if (style === 'dotted') return `${Math.max(1, width)} ${width * 2.5}`
  return undefined
}

/** The topmost of the shapes (drawn in this order) that contains the point, within `tolerance`. */
export function topmostAt<T extends { shape: Shape }>(shapes: readonly T[], p: Point, tolerance = 0): T | null {
  for (let i = shapes.length - 1; i >= 0; i--) if (containsPoint(shapes[i]!.shape, p, tolerance)) return shapes[i]!
  return null
}

/** Distance from a point to a segment (clicking a line). */
export function distanceToSegment(p: Point, a: Point, b: Point): number {
  const dx = b.x - a.x
  const dy = b.y - a.y
  const length = dx * dx + dy * dy
  const t = length ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / length)) : 0
  return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy))
}

export interface TimelineMark {
  id: string
  kind: 'milestone' | 'gate'
  /** Relative to the frame's top-left corner. */
  x: number
  y: number
  title: string
  date: string | null
}

export interface TimelineLayout {
  axis: { x1: number; y1: number; x2: number; y2: number }
  marks: TimelineMark[]
}

/** Where a milestone timeline puts its marks within its frame (doc 01 §12.3): along its axis, evenly or by date. */
export function timelineLayout(
  milestones: readonly MilestoneRow[],
  frame: Pick<Rect, 'width' | 'height'>,
  config: { orientation?: 'horizontal' | 'vertical'; spacing?: 'even' | 'date' } | null | undefined,
): TimelineLayout {
  const vertical = config?.orientation === 'vertical'
  const pad = 24
  const length = Math.max(1, (vertical ? frame.height : frame.width) - 2 * pad)
  const across = vertical ? Math.min(pad, frame.width / 2) : frame.height / 2
  const n = milestones.length
  const even = (i: number, count: number) => (count > 1 ? i / (count - 1) : 0.5)
  let positions: number[]
  if (config?.spacing === 'date') {
    const dated = milestones.map((m) => (m.due_date ? Date.parse(m.due_date) : NaN))
    const valid = dated.filter((t) => Number.isFinite(t))
    const undated = dated.length - valid.length
    const lo = Math.min(...valid)
    const hi = Math.max(...valid)
    const span = undated ? 0.85 : 1
    let u = 0
    positions = dated.map((t) => {
      if (!Number.isFinite(t)) return undated === 1 ? 1 : 0.9 + 0.1 * even(u++, undated)
      return hi > lo ? ((t - lo) / (hi - lo)) * span : valid.length ? span / 2 : 0.5
    })
  } else {
    positions = milestones.map((_, i) => even(i, n))
  }
  const marks = milestones.map((m, i): TimelineMark => {
    const along = pad + positions[i]! * length
    return {
      id: m.id,
      kind: m.kind,
      x: vertical ? across : along,
      y: vertical ? along : across,
      title: m.title,
      date: m.due_date ?? null,
    }
  })
  const axis = vertical
    ? { x1: across, y1: pad, x2: across, y2: pad + length }
    : { x1: pad, y1: across, x2: pad + length, y2: across }
  return { axis, marks }
}
