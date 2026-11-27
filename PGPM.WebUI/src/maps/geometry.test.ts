// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  bandBox,
  bandMode,
  bendOf,
  borderPoint,
  boundsOf,
  boundsOfCircles,
  boxesOverlap,
  boxOf,
  centreOf,
  connectorEnds,
  containsPoint,
  edgePath,
  hitsBand,
  padBox,
  rectOf,
  selectInBand,
  snap,
  snapPoint,
  snapRect,
  type Point,
  type Shape,
} from './geometry'

const close = (p: Point, x: number, y: number) => {
  expect(p.x).toBeCloseTo(x, 9)
  expect(p.y).toBeCloseTo(y, 9)
}

/** A 200 × 100 frame centred on (0, 0). */
const frame = (kind: Shape['kind'], radius?: number): Shape => ({
  kind,
  x: -100,
  y: -50,
  width: 200,
  height: 100,
  radius,
})

describe('drawing a graph (from PNMTD)', () => {
  it('draws a lone edge straight and bends parallel ones apart, symmetrically', () => {
    expect(edgePath({ x: 0, y: 0 }, { x: 10, y: 0 })).toBe('M 0 0 L 10 0')
    expect(bendOf(0, 1)).toBe(0)
    expect([bendOf(0, 2), bendOf(1, 2)]).toEqual([-9, 9])
    expect([bendOf(0, 3), bendOf(1, 3), bendOf(2, 3)]).toEqual([-18, 0, 18])
    expect(edgePath({ x: 0, y: 0 }, { x: 10, y: 0 }, 5)).toBe('M 0 0 Q 5 5 10 0')
  })

  it('boxes the circles, or falls back when there are none', () => {
    const fallback = { minX: -1, minY: -1, maxX: 1, maxY: 1 }
    expect(boundsOfCircles([], fallback)).toEqual(fallback)
    expect(
      boundsOfCircles(
        [
          { x: 0, y: 0, r: 5 },
          { x: 20, y: -10, r: 2 },
        ],
        fallback,
      ),
    ).toEqual({ minX: -5, minY: -12, maxX: 22, maxY: 5 })
  })
})

describe('boxes', () => {
  it('bounds a set of frames and points, or falls back', () => {
    const fallback = { minX: 0, minY: 0, maxX: 1, maxY: 1 }
    expect(boundsOf([], fallback)).toBe(fallback)
    expect(
      boundsOf(
        [
          { x: 10, y: 10, width: 100, height: 40 },
          { x: -20, y: 30, width: 10, height: 50 },
          { x: 300, y: -5 },
        ],
        fallback,
      ),
    ).toEqual({ minX: -20, minY: -5, maxX: 300, maxY: 80 })
  })

  it('converts between frames and boxes', () => {
    const r = { x: 10, y: 20, width: 30, height: 40 }
    expect(boxOf(r)).toEqual({ minX: 10, minY: 20, maxX: 40, maxY: 60 })
    expect(rectOf(boxOf(r))).toEqual(r)
    expect(centreOf(r)).toEqual({ x: 25, y: 40 })
    expect(padBox(boxOf(r), 5)).toEqual({ minX: 5, minY: 15, maxX: 45, maxY: 65 })
  })

  it('tells overlapping boxes from touching ones, with a gap', () => {
    const a = { minX: 0, minY: 0, maxX: 10, maxY: 10 }
    expect(boxesOverlap(a, { minX: 5, minY: 5, maxX: 15, maxY: 15 })).toBe(true)
    expect(boxesOverlap(a, { minX: 10, minY: 0, maxX: 20, maxY: 10 })).toBe(false)
    expect(boxesOverlap(a, { minX: 12, minY: 0, maxX: 20, maxY: 10 }, 3)).toBe(true)
    expect(boxesOverlap(a, { minX: 0, minY: 11, maxX: 10, maxY: 20 }, 1)).toBe(false)
  })
})

describe('connector ends on borders', () => {
  it('meets a rectangle on its edges and corners', () => {
    const r = frame('rect')
    close(borderPoint(r, { x: 500, y: 0 }), 100, 0)
    close(borderPoint(r, { x: 0, y: -500 }), 0, -50)
    close(borderPoint(r, { x: 300, y: 300 }), 50, 50)
    close(borderPoint(r, { x: -400, y: -200 }), -100, -50)
    close(borderPoint(r, { x: 0, y: 0 }), 0, 0)
  })

  it('meets an ellipse on its curve', () => {
    const e = frame('ellipse')
    close(borderPoint(e, { x: 500, y: 0 }), 100, 0)
    close(borderPoint(e, { x: 0, y: 70 }), 0, 50)
    const p = borderPoint(e, { x: 100, y: 100 })
    expect((p.x / 100) ** 2 + (p.y / 50) ** 2).toBeCloseTo(1, 9)
    expect(p.x).toBeCloseTo(p.y, 9)
  })

  it('meets a diamond on its sides', () => {
    const d = frame('diamond')
    close(borderPoint(d, { x: 500, y: 0 }), 100, 0)
    close(borderPoint(d, { x: 0, y: -1 }), 0, -50)
    const p = borderPoint(d, { x: 10, y: 10 })
    expect(Math.abs(p.x) / 100 + Math.abs(p.y) / 50).toBeCloseTo(1, 9)
    close(p, 100 / 3, 100 / 3)
  })

  it('meets a rounded rectangle on its straight edges and on the corner arcs', () => {
    const r = frame('roundedRect', 20)
    close(borderPoint(r, { x: 500, y: 0 }), 100, 0)
    close(borderPoint(r, { x: 500, y: 100 }), 100, 20)
    // Towards the corner: on the arc around (80, 30), radius 20.
    const p = borderPoint(r, { x: 1000, y: 500 })
    expect(Math.hypot(p.x - 80, p.y - 30)).toBeCloseTo(20, 9)
    expect(p.x).toBeGreaterThan(80)
    expect(p.y / p.x).toBeCloseTo(0.5, 9)
    const q = borderPoint(r, { x: -1000, y: -500 })
    close(q, -p.x, -p.y)
    // Without a radius it is a rectangle; a radius beyond half the side is half the side.
    close(borderPoint(frame('roundedRect'), { x: 300, y: 300 }), 50, 50)
    const pill = borderPoint(frame('roundedRect', 500), { x: 0, y: 500 })
    close(pill, 0, 50)
  })

  it('connects two shapes facing each other, or a shape and a free end', () => {
    const a: Shape = { kind: 'rect', x: 0, y: 0, width: 100, height: 50 }
    const b: Shape = { kind: 'ellipse', x: 300, y: 0, width: 100, height: 50 }
    const ends = connectorEnds(a, b)
    close(ends.start, 100, 25)
    close(ends.end, 300, 25)
    const free = connectorEnds(a, { x: 50, y: 300 })
    close(free.start, 50, 50)
    expect(free.end).toEqual({ x: 50, y: 300 })
    expect(connectorEnds({ x: 1, y: 2 }, { x: 3, y: 4 })).toEqual({ start: { x: 1, y: 2 }, end: { x: 3, y: 4 } })
  })
})

describe('points in shapes', () => {
  it('rect: inside, on the border, outside, with a tolerance', () => {
    const r = frame('rect')
    expect(containsPoint(r, { x: 0, y: 0 })).toBe(true)
    expect(containsPoint(r, { x: 100, y: 50 })).toBe(true)
    expect(containsPoint(r, { x: 101, y: 0 })).toBe(false)
    expect(containsPoint(r, { x: 101, y: 0 }, 2)).toBe(true)
  })

  it('rounded rect: the corners are cut', () => {
    const r = frame('roundedRect', 20)
    expect(containsPoint(r, { x: 99, y: 0 })).toBe(true)
    expect(containsPoint(r, { x: 99, y: 49 })).toBe(false)
    expect(containsPoint(r, { x: 90, y: 40 })).toBe(true)
    expect(containsPoint(r, { x: 80 + 14, y: 30 + 14 })).toBe(true)
    expect(containsPoint(r, { x: 80 + 15, y: 30 + 15 })).toBe(false)
    expect(containsPoint(r, { x: 80 + 15, y: 30 + 15 }, 2)).toBe(true)
  })

  it('ellipse and diamond', () => {
    const e = frame('ellipse')
    expect(containsPoint(e, { x: 99, y: 0 })).toBe(true)
    expect(containsPoint(e, { x: 90, y: 40 })).toBe(false)
    expect(containsPoint(e, { x: 70, y: 35 })).toBe(true)
    const d = frame('diamond')
    expect(containsPoint(d, { x: 50, y: 25 })).toBe(true)
    expect(containsPoint(d, { x: 60, y: 25 })).toBe(false)
    expect(containsPoint(d, { x: 0, y: -50 })).toBe(true)
  })
})

describe('rubber band', () => {
  const items = [
    { id: 'a', x: 10, y: 10, width: 20, height: 20 },
    { id: 'b', x: 50, y: 10, width: 100, height: 20 },
    { id: 'c', x: 300, y: 300, width: 10, height: 10 },
  ]

  it('spans the band from either corner, contains to the right and touches to the left', () => {
    expect(bandBox({ x: 100, y: 50 }, { x: 0, y: 0 })).toEqual({ minX: 0, minY: 0, maxX: 100, maxY: 50 })
    expect(bandMode({ x: 0, y: 0 }, { x: 100, y: 50 })).toBe('contain')
    expect(bandMode({ x: 100, y: 0 }, { x: 0, y: 50 })).toBe('touch')
  })

  it('selects frames wholly inside, or touched', () => {
    const band = bandBox({ x: 0, y: 0 }, { x: 100, y: 50 })
    expect(selectInBand(band, items, 'contain')).toEqual(['a'])
    expect(selectInBand(band, items, 'touch')).toEqual(['a', 'b'])
    expect(hitsBand(band, { x: 100, y: 50, width: 5, height: 5 }, 'touch')).toBe(true)
    expect(hitsBand(band, { x: 0, y: 0, width: 100, height: 50 }, 'contain')).toBe(true)
    expect(selectInBand(bandBox({ x: 500, y: 500 }, { x: 600, y: 600 }), items, 'touch')).toEqual([])
  })
})

describe('grid', () => {
  it('snaps values, points and frames to the grid', () => {
    expect(snap(29, 20)).toBe(20)
    expect(snap(30, 20)).toBe(40)
    expect(snap(-29, 20)).toBe(-20)
    expect(Object.is(snap(-4, 20), 0)).toBe(true)
    expect(snap(13.7, 0)).toBe(13.7)
    expect(snapPoint({ x: 11, y: -11 }, 10)).toEqual({ x: 10, y: -10 })
    expect(snapRect({ x: 12, y: 18, width: 95, height: 3 }, 20)).toEqual({ x: 20, y: 20, width: 80, height: 20 })
    const r = { x: 1, y: 2, width: 3, height: 4 }
    expect(snapRect(r, 0)).toBe(r)
  })
})
