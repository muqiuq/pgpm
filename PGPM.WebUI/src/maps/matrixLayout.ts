import { boxesOverlap, type Box, type Point } from './geometry'

/**
 * Matrix maps (docs/03 §13.5, data model §9 `maps.matrix`, doc 01 §12.8): item cards placed by two field values in a
 * fixed 2000 × 1400 world box – x to the right, y upwards – nudged apart where they overlap, by a spiral search that
 * depends on nothing but the items (in the order of their ids, each takes the free place nearest to its values), so
 * every device draws the same picture. Items without both values go to the tray *Not yet assessed*. The values come from the caller (score.ts
 * for scores); this module only lays out.
 */

export type Quadrant = 'tl' | 'tr' | 'bl' | 'br'

export interface MatrixAxis {
  field: string
  label?: string
  min: number
  max: number
  /** The split line; default the middle of min and max. */
  split?: number | null
}

/** `maps.matrix` as far as the layout needs it. */
export interface MatrixConfig {
  x: MatrixAxis
  y: MatrixAxis
  /** Bubble size: cards grow with this field's value. */
  size?: { field: string } | null
  quadrants?: Partial<Record<Quadrant, string>> | null
}

/** An item with its values on the two axes (null: not assessed) and its bubble size value. */
export interface MatrixItem {
  id: string
  x: number | null
  y: number | null
  size?: number | null
}

export interface MatrixCard {
  id: string
  /** The card's frame: top-left corner and size. */
  x: number
  y: number
  width: number
  height: number
  /** Its centre, and where its values put it (the centre before nudging). */
  cx: number
  cy: number
  targetX: number
  targetY: number
  /** The card's size relative to a standard card: bubble size × the shrink for crowded maps. */
  scale: number
  quadrant: Quadrant
}

export interface MatrixLayout {
  /** The world box, and the plot inside its padding (min/max of both axes at its edges). */
  world: Box
  plot: Box
  /** Where the split lines are drawn: x of the vertical one, y of the horizontal one. */
  split: Point
  /** The quadrants' parts of the plot, for the background and its labels. */
  quadrants: Record<Quadrant, Box>
  /** Sorted by id. */
  cards: MatrixCard[]
  /** Not yet assessed, in the order given. */
  tray: MatrixItem[]
}

export const WORLD_WIDTH = 2000
export const WORLD_HEIGHT = 1400
export const PADDING = 100
/** A standard card on a matrix map (compact: title and badges). */
export const CARD_WIDTH = 180
export const CARD_HEIGHT = 56
/** The least distance between two cards after nudging. */
export const CARD_GAP = 6
/** Bubble sizes: the smallest (no or the least value) and the largest card. */
export const BUBBLE_MIN = 0.8
export const BUBBLE_MAX = 1.6
/** Cards cover at most this part of the plot; more cards shrink all of them alike. */
export const MAX_FILL = 0.35

export const DEFAULT_QUADRANT_LABELS: Readonly<Record<Quadrant, string>> = {
  tr: 'Star initiatives',
  br: 'Quick wins',
  tl: 'Difficult but impactful',
  bl: 'Limited interest',
}

const GOLDEN_ANGLE = Math.PI * (3 - Math.sqrt(5))
const SPIRAL_STEPS = 6000

function finite(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/** The axis' split line; the middle of min and max when none is set. */
export function splitOf(axis: MatrixAxis): number {
  return finite(axis.split) ? axis.split : (axis.min + axis.max) / 2
}

/** Where a value lies on the axis, 0 at min, 1 at max (also for a reversed axis, max < min), clamped. */
export function axisPosition(value: number, axis: MatrixAxis): number {
  if (!finite(axis.min) || !finite(axis.max) || axis.max === axis.min) return 0.5
  return Math.min(1, Math.max(0, (value - axis.min) / (axis.max - axis.min)))
}

/** On the axis' high side: at the split or beyond it towards max. */
function high(value: number, axis: MatrixAxis): boolean {
  return (value - splitOf(axis)) * (axis.max < axis.min ? -1 : 1) >= 0
}

/**
 * The quadrant of an item's values on a matrix map, with its label (the map's, or the default): right/top is the high
 * side of an axis, a value on the split line counts as high. Null when a value is missing.
 */
export function quadrantOf(
  x: number | null | undefined,
  y: number | null | undefined,
  config: MatrixConfig,
): { quadrant: Quadrant; label: string } | null {
  if (!finite(x) || !finite(y)) return null
  const quadrant = `${high(y, config.y) ? 't' : 'b'}${high(x, config.x) ? 'r' : 'l'}` as Quadrant
  return { quadrant, label: config.quadrants?.[quadrant] || DEFAULT_QUADRANT_LABELS[quadrant] }
}

/** A bubble's scale for its size value: area proportional to the value, from BUBBLE_MIN (none, ≤ 0) to BUBBLE_MAX. */
export function bubbleScale(value: number | null | undefined, largest: number): number {
  if (!finite(value) || value <= 0 || !(largest > 0)) return BUBBLE_MIN
  return BUBBLE_MIN + (BUBBLE_MAX - BUBBLE_MIN) * Math.sqrt(Math.min(1, value / largest))
}

function compareIds(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

export const WORLD: Readonly<Box> = { minX: 0, minY: 0, maxX: WORLD_WIDTH, maxY: WORLD_HEIGHT }
export const PLOT: Readonly<Box> = {
  minX: PADDING,
  minY: PADDING,
  maxX: WORLD_WIDTH - PADDING,
  maxY: WORLD_HEIGHT - PADDING,
}

/** The world point of an item's values: the centre its card aims for (values beyond min/max: on the plot's edge). */
export function matrixPoint(x: number, y: number, config: MatrixConfig): Point {
  const tx = axisPosition(x, config.x)
  const ty = axisPosition(y, config.y)
  return { x: PLOT.minX + tx * (PLOT.maxX - PLOT.minX), y: PLOT.maxY - ty * (PLOT.maxY - PLOT.minY) }
}

/** Card positions, the tray and the background's frame for a matrix map. */
export function layoutMatrix(items: readonly MatrixItem[], config: MatrixConfig): MatrixLayout {
  const world = { ...WORLD }
  const plot = { ...PLOT }
  const split = matrixPoint(splitOf(config.x), splitOf(config.y), config)
  const quadrants: Record<Quadrant, Box> = {
    tl: { minX: plot.minX, minY: plot.minY, maxX: split.x, maxY: split.y },
    tr: { minX: split.x, minY: plot.minY, maxX: plot.maxX, maxY: split.y },
    bl: { minX: plot.minX, minY: split.y, maxX: split.x, maxY: plot.maxY },
    br: { minX: split.x, minY: split.y, maxX: plot.maxX, maxY: plot.maxY },
  }

  const tray: MatrixItem[] = []
  const assessed: (MatrixItem & { x: number; y: number })[] = []
  for (const item of items) {
    if (finite(item.x) && finite(item.y)) assessed.push(item as MatrixItem & { x: number; y: number })
    else tray.push(item)
  }
  assessed.sort((a, b) => compareIds(a.id, b.id))

  const sizes = config.size ? assessed.map((i) => i.size).filter((s): s is number => finite(s) && s > 0) : []
  const largest = sizes.length ? Math.max(...sizes) : 0
  const bubbles = assessed.map((i) => (config.size ? bubbleScale(i.size, largest) : 1))
  const area = bubbles.reduce((sum, s) => sum + CARD_WIDTH * s * CARD_HEIGHT * s, 0)
  const plotArea = (plot.maxX - plot.minX) * (plot.maxY - plot.minY)
  const shrink = area > 0 ? Math.min(1, Math.sqrt((MAX_FILL * plotArea) / area)) : 1

  const placer = new Placer(world)
  const cards = assessed.map((item, index): MatrixCard => {
    const scale = bubbles[index]! * shrink
    const width = CARD_WIDTH * scale
    const height = CARD_HEIGHT * scale
    const target = matrixPoint(item.x, item.y, config)
    const centre = placer.place(target, width, height)
    return {
      id: item.id,
      x: centre.x - width / 2,
      y: centre.y - height / 2,
      width,
      height,
      cx: centre.x,
      cy: centre.y,
      targetX: target.x,
      targetY: target.y,
      scale,
      quadrant: quadrantOf(item.x, item.y, config)!.quadrant,
    }
  })
  return { world, plot, split, quadrants, cards, tray }
}

/** The cards placed so far, in a grid of buckets: a free spot is checked against the cards near it only. */
class Placed {
  private readonly buckets = new Map<number, Box[]>()

  private static readonly CELL = 100

  private *cells(b: Box): Generator<number> {
    const cell = Placed.CELL
    for (let x = Math.floor(b.minX / cell); x <= Math.floor(b.maxX / cell); x++) {
      for (let y = Math.floor(b.minY / cell); y <= Math.floor(b.maxY / cell); y++) yield (x + 1024) * 4096 + (y + 1024)
    }
  }

  add(b: Box) {
    for (const key of this.cells(b)) {
      const bucket = this.buckets.get(key)
      if (bucket) bucket.push(b)
      else this.buckets.set(key, [b])
    }
  }

  /** A box keeps at least `gap` from every placed one. */
  isFree(b: Box, gap: number): boolean {
    const near = { minX: b.minX - gap, minY: b.minY - gap, maxX: b.maxX + gap, maxY: b.maxY + gap }
    for (const key of this.cells(near)) {
      for (const other of this.buckets.get(key) ?? []) if (boxesOverlap(b, other, gap)) return false
    }
    return true
  }
}

/**
 * The centres a card of this size may take around `target`, nearest first: the target (kept in the world), a sunflower
 * spiral stretched to the card's shape (even steps over the area), then a grid over the whole world by distance.
 */
function* candidatesAround(target: Point, width: number, height: number, world: Box): Generator<Point> {
  const clamp = (p: Point): Point => ({
    x: Math.min(world.maxX - width / 2, Math.max(world.minX + width / 2, p.x)),
    y: Math.min(world.maxY - height / 2, Math.max(world.minY + height / 2, p.y)),
  })
  yield clamp(target)
  const mean = (width + height) / 2
  const step = Math.min(width, height) / 4
  for (let k = 1; k <= SPIRAL_STEPS; k++) {
    const r = step * Math.sqrt(k)
    const angle = k * GOLDEN_ANGLE
    yield clamp({
      x: target.x + r * Math.cos(angle) * (width / mean),
      y: target.y + r * Math.sin(angle) * (height / mean),
    })
  }
  const grid: Point[] = []
  for (let y = world.minY + height / 2; y <= world.maxY - height / 2; y += height / 2) {
    for (let x = world.minX + width / 2; x <= world.maxX - width / 2; x += width / 4) grid.push({ x, y })
  }
  const distance = (p: Point) => Math.hypot(p.x - target.x, p.y - target.y)
  yield* grid.sort((a, b) => distance(a) - distance(b))
}

/**
 * Places cards in id order, each at the first free candidate around its target. Cards with the same target and size
 * (items with the same values) share one search: what was taken for one is taken for the next, so it goes on from there.
 */
class Placer {
  private readonly placed = new Placed()
  private readonly searches = new Map<string, Generator<Point>>()

  constructor(private readonly world: Box) {}

  /** The centre of the card's place. */
  place(target: Point, width: number, height: number): Point {
    const key = `${target.x} ${target.y} ${width} ${height}`
    let search = this.searches.get(key)
    if (!search) {
      search = candidatesAround(target, width, height, this.world)
      this.searches.set(key, search)
    }
    const boxAt = (c: Point): Box => ({
      minX: c.x - width / 2,
      minY: c.y - height / 2,
      maxX: c.x + width / 2,
      maxY: c.y + height / 2,
    })
    let centre: Point | null = null
    // next() rather than for…of: leaving a for…of loop would close the search for the cards that share it.
    for (let next = search.next(); !next.done; next = search.next()) {
      if (this.placed.isFree(boxAt(next.value), CARD_GAP)) {
        centre = next.value
        break
      }
    }
    // A full world (it can't be at MAX_FILL): on the target, overlapping.
    centre ??= target
    this.placed.add(boxAt(centre))
    return centre
  }
}
