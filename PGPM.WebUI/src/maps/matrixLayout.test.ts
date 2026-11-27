// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { boxesOverlap } from './geometry'
import {
  axisPosition,
  BUBBLE_MAX,
  BUBBLE_MIN,
  bubbleScale,
  CARD_GAP,
  CARD_HEIGHT,
  CARD_WIDTH,
  layoutMatrix,
  matrixPoint,
  PADDING,
  quadrantOf,
  splitOf,
  WORLD_HEIGHT,
  WORLD_WIDTH,
  type MatrixCard,
  type MatrixConfig,
  type MatrixItem,
} from './matrixLayout'

/** The prioritisation matrix of doc 04 §2.6 (data model §9). */
const config: MatrixConfig = {
  x: { field: 'feasibility_index', label: 'Ease of implementation', min: 0, max: 65, split: 32.5 },
  y: { field: 'impact_score', label: 'Business impact', min: 0, max: 100, split: 50 },
  size: { field: 'business_value_chf' },
  quadrants: { tr: 'Star initiatives', br: 'Quick wins', tl: 'Difficult but impactful', bl: 'Limited interest' },
}

/** A seeded generator (mulberry32): the same "random" items on every run. */
function random(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function uuid(n: number): string {
  return `0193f3c2-7d1e-7c55-9a35-${n.toString(16).padStart(12, '0')}`
}

function randomItems(count: number, seed: number): MatrixItem[] {
  const next = random(seed)
  return Array.from({ length: count }, (_, i) => ({
    id: uuid(Math.floor(next() * 1e9) * 1000 + i),
    x: Math.round(next() * 65),
    y: Math.round(next() * 100),
    size: next() < 0.2 ? null : Math.round(next() * 2_000_000),
  }))
}

function overlaps(cards: MatrixCard[]): [string, string][] {
  const box = (c: MatrixCard) => ({ minX: c.x, minY: c.y, maxX: c.x + c.width, maxY: c.y + c.height })
  const pairs: [string, string][] = []
  for (let i = 0; i < cards.length; i++) {
    for (let j = i + 1; j < cards.length; j++) {
      if (boxesOverlap(box(cards[i]!), box(cards[j]!), CARD_GAP - 0.001)) pairs.push([cards[i]!.id, cards[j]!.id])
    }
  }
  return pairs
}

function insideWorld(c: MatrixCard): boolean {
  return c.x >= -1e-9 && c.y >= -1e-9 && c.x + c.width <= WORLD_WIDTH + 1e-9 && c.y + c.height <= WORLD_HEIGHT + 1e-9
}

describe('quadrants (doc 01 §12.8)', () => {
  it('names the quadrant of an item, right and top being high', () => {
    expect(quadrantOf(58, 72, config)).toEqual({ quadrant: 'tr', label: 'Star initiatives' })
    expect(quadrantOf(58, 20, config)).toEqual({ quadrant: 'br', label: 'Quick wins' })
    expect(quadrantOf(10, 72, config)).toEqual({ quadrant: 'tl', label: 'Difficult but impactful' })
    expect(quadrantOf(10, 20, config)).toEqual({ quadrant: 'bl', label: 'Limited interest' })
  })

  it('counts a value on the split line as high, and has no quadrant without both values', () => {
    expect(quadrantOf(32.5, 50, config)?.quadrant).toBe('tr')
    expect(quadrantOf(32.49, 49.99, config)?.quadrant).toBe('bl')
    expect(quadrantOf(null, 50, config)).toBeNull()
    expect(quadrantOf(40, undefined, config)).toBeNull()
    expect(quadrantOf(Number.NaN, 50, config)).toBeNull()
  })

  it('splits in the middle by default, uses the default labels, and follows a reversed axis', () => {
    const plain: MatrixConfig = { x: { field: 'a', min: 0, max: 10 }, y: { field: 'b', min: 0, max: 10, split: null } }
    expect(splitOf(plain.x)).toBe(5)
    expect(quadrantOf(6, 4, plain)).toEqual({ quadrant: 'br', label: 'Quick wins' })
    const reversed: MatrixConfig = { ...plain, x: { field: 'cost', min: 100, max: 0 }, quadrants: { br: '' } }
    expect(quadrantOf(20, 4, reversed)).toEqual({ quadrant: 'br', label: 'Quick wins' })
    expect(quadrantOf(80, 4, reversed)?.quadrant).toBe('bl')
    expect(axisPosition(20, reversed.x)).toBeCloseTo(0.8)
  })
})

describe('positions', () => {
  it('maps values into the plot inside the padding, y upwards, clamped', () => {
    expect(matrixPoint(0, 0, config)).toEqual({ x: PADDING, y: WORLD_HEIGHT - PADDING })
    expect(matrixPoint(65, 100, config)).toEqual({ x: WORLD_WIDTH - PADDING, y: PADDING })
    expect(matrixPoint(32.5, 50, config)).toEqual({ x: WORLD_WIDTH / 2, y: WORLD_HEIGHT / 2 })
    expect(matrixPoint(-10, 250, config)).toEqual({ x: PADDING, y: PADDING })
    const flat: MatrixConfig = { x: { field: 'a', min: 3, max: 3 }, y: { field: 'b', min: 0, max: 1 } }
    expect(matrixPoint(3, 0, flat).x).toBe(WORLD_WIDTH / 2)
  })

  it('scales bubbles by area, between their limits', () => {
    expect(bubbleScale(null, 100)).toBe(BUBBLE_MIN)
    expect(bubbleScale(0, 100)).toBe(BUBBLE_MIN)
    expect(bubbleScale(-5, 100)).toBe(BUBBLE_MIN)
    expect(bubbleScale(100, 100)).toBe(BUBBLE_MAX)
    expect(bubbleScale(25, 100)).toBeCloseTo(BUBBLE_MIN + (BUBBLE_MAX - BUBBLE_MIN) / 2)
    expect(bubbleScale(500, 100)).toBe(BUBBLE_MAX)
    expect(bubbleScale(5, 0)).toBe(BUBBLE_MIN)
  })
})

describe('layout (docs/03 §13.5)', () => {
  it('places a few cards on their values, the rest in the tray', () => {
    const items: MatrixItem[] = [
      { id: 'b', x: 58, y: 72, size: 1_000_000 },
      { id: 'a', x: 10, y: 20, size: 250_000 },
      { id: 'c', x: null, y: 40 },
      { id: 'd', x: 30, y: null },
      { id: 'e', x: null, y: null },
    ]
    const layout = layoutMatrix(items, config)
    expect(layout.tray.map((i) => i.id)).toEqual(['c', 'd', 'e'])
    expect(layout.cards.map((c) => c.id)).toEqual(['a', 'b'])
    const [a, b] = layout.cards as [MatrixCard, MatrixCard]
    expect({ x: b.cx, y: b.cy }).toEqual(matrixPoint(58, 72, config))
    expect({ x: b.targetX, y: b.targetY }).toEqual({ x: b.cx, y: b.cy })
    expect(b.scale).toBe(BUBBLE_MAX)
    expect(b.width).toBeCloseTo(CARD_WIDTH * BUBBLE_MAX)
    expect(b.x).toBeCloseTo(b.cx - b.width / 2)
    expect(a.scale).toBeCloseTo(BUBBLE_MIN + (BUBBLE_MAX - BUBBLE_MIN) / 2)
    expect([a.quadrant, b.quadrant]).toEqual(['bl', 'tr'])
  })

  it('frames the background: plot, split lines and quadrants', () => {
    const layout = layoutMatrix([], config)
    expect(layout.world).toEqual({ minX: 0, minY: 0, maxX: WORLD_WIDTH, maxY: WORLD_HEIGHT })
    expect(layout.plot).toEqual({
      minX: PADDING,
      minY: PADDING,
      maxX: WORLD_WIDTH - PADDING,
      maxY: WORLD_HEIGHT - PADDING,
    })
    expect(layout.split).toEqual({ x: WORLD_WIDTH / 2, y: WORLD_HEIGHT / 2 })
    expect(layout.quadrants.tr).toEqual({
      minX: WORLD_WIDTH / 2,
      minY: PADDING,
      maxX: WORLD_WIDTH - PADDING,
      maxY: WORLD_HEIGHT / 2,
    })
    expect(layout.quadrants.bl).toEqual({
      minX: PADDING,
      minY: WORLD_HEIGHT / 2,
      maxX: WORLD_WIDTH / 2,
      maxY: WORLD_HEIGHT - PADDING,
    })
    expect(layout.cards).toEqual([])
  })

  it('makes every card the same size without a size field', () => {
    const layout = layoutMatrix(randomItems(10, 3), { ...config, size: null })
    expect(new Set(layout.cards.map((c) => c.width))).toEqual(new Set([CARD_WIDTH]))
    expect(new Set(layout.cards.map((c) => c.height))).toEqual(new Set([CARD_HEIGHT]))
  })

  it('nudges cards on the same spot apart, the first by id staying on it', () => {
    const items = Array.from({ length: 40 }, (_, i) => ({ id: uuid(1000 - i), x: 40, y: 60 }))
    const layout = layoutMatrix(items, { ...config, size: null })
    expect(overlaps(layout.cards)).toEqual([])
    const first = layout.cards[0]!
    expect(first.id).toBe(uuid(961))
    expect({ x: first.cx, y: first.cy }).toEqual(matrixPoint(40, 60, config))
    // Nudged, not scattered: all around the spot (40 standard cards fill about 1300 × 400 units).
    const distances = layout.cards.map((c) => Math.hypot(c.cx - first.cx, c.cy - first.cy))
    expect(Math.max(...distances)).toBeLessThan(900)
    expect(distances.filter((d) => d < 400).length).toBeGreaterThan(20)
  })

  it('places 200 random items without overlaps, inside the world', () => {
    for (const seed of [1, 2, 3]) {
      const layout = layoutMatrix(randomItems(200, seed), config)
      expect(layout.cards).toHaveLength(200)
      expect(overlaps(layout.cards)).toEqual([])
      expect(layout.cards.every(insideWorld)).toBe(true)
      // A crowded map shrinks its cards rather than scattering them.
      expect(Math.max(...layout.cards.map((c) => c.scale))).toBeLessThan(BUBBLE_MAX)
      const far = layout.cards.filter((c) => Math.hypot(c.cx - c.targetX, c.cy - c.targetY) > 300)
      expect(far.length).toBeLessThan(10)
    }
  })

  it('keeps cards in the world on the plot edges and in the corners', () => {
    const corners = [0, 65].flatMap((x) =>
      [0, 100].flatMap((y) => Array.from({ length: 15 }, (_, i) => ({ id: `${x}-${y}-${i}`, x, y }))),
    )
    const layout = layoutMatrix(corners, config)
    expect(overlaps(layout.cards)).toEqual([])
    expect(layout.cards.every(insideWorld)).toBe(true)
  })

  it('is deterministic: the same layout for the same items in any order', () => {
    const items = randomItems(120, 9)
    const once = layoutMatrix(items, config)
    const shuffled = [...items].reverse()
    shuffled.push(shuffled.shift()!)
    const again = layoutMatrix(shuffled, config)
    expect(again.cards).toEqual(once.cards)
    expect(layoutMatrix(items, config)).toEqual(once)
  })

  it('moves only what changed nearby when one value changes', () => {
    const items = randomItems(30, 5)
    const before = layoutMatrix(items, config)
    const changed = items.map((i, n) => (n === 0 ? { ...i, x: 5, y: 5 } : i))
    const after = layoutMatrix(changed, config)
    const same = after.cards.filter((c) => {
      const old = before.cards.find((o) => o.id === c.id)!
      return old.cx === c.cx && old.cy === c.cy
    })
    expect(same.length).toBeGreaterThan(20)
  })
})
