import { describe, expect, it } from 'vitest'
import type { Rect } from './geometry'
import { arrowHead, lineGeometry, lineRow, shapeOf, storedEnds, timelineLayout } from './scene'
import { C1, L1, R1, setup } from './testFixtures'
import type { MilestoneRow } from './types'

describe('lines', () => {
  it('stores a line by its box with points relative to it', () => {
    const row = lineRow({ x: 10, y: 50 }, { x: 4, y: 20 })
    expect(row).toEqual({
      x: 4,
      y: 20,
      width: 6,
      height: 30,
      points: [
        [6, 30],
        [0, 0],
      ],
    })
    expect(storedEnds(row)).toEqual([
      { x: 10, y: 50 },
      { x: 4, y: 20 },
    ])
  })

  it('puts connected ends on the borders, facing each other', () => {
    const a = shapeOf({ kind: 'rect', style: {} }, { x: 0, y: 0, width: 100, height: 100 })
    const b = shapeOf({ kind: 'ellipse', style: {} }, { x: 300, y: 0, width: 100, height: 100 })
    const g = lineGeometry(a, b, { arrowEnd: 'triangle' })
    expect(g.start).toEqual({ x: 100, y: 50 })
    expect(g.end).toEqual({ x: 300, y: 50 })
    expect(g.heads).toHaveLength(1)
    expect(g.heads[0]!.kind).toBe('triangle')
    // The stroke stops short of a triangle's tip.
    expect(g.d.endsWith('300 50')).toBe(false)
  })

  it('routes an elbow line from the middle of the facing sides', () => {
    const a = shapeOf({ kind: 'rect', style: {} }, { x: 0, y: 0, width: 100, height: 100 })
    const b = shapeOf({ kind: 'rect', style: {} }, { x: 300, y: 200, width: 100, height: 100 })
    const g = lineGeometry(a, b, { lineType: 'elbow' })
    expect(g.start).toEqual({ x: 100, y: 50 })
    expect(g.end).toEqual({ x: 300, y: 250 })
    expect(g.d).toBe('M 100 50 L 200 50 L 200 250 L 300 250')
  })

  it('draws arrow heads pointing along the line', () => {
    const head = arrowHead('arrow', { x: 100, y: 0 }, { x: 1, y: 0 }, 2)
    expect(head.d).toBe('M 92 4.4 L 100 0 L 92 -4.4')
  })

  it('connected ends follow their elements when they move', () => {
    const s = setup()
    const line = s.row(L1)!
    const before = s.model.lineOf(line)
    // The card's bottom border, below its centre (100, 40); the rectangle's top (y 300).
    expect(before.start.y).toBeCloseTo(80, 5)
    expect(before.end.y).toBeCloseTo(300, 5)
    const frames = new Map<string, Rect>([
      [C1, { x: 500, y: 0, width: 200, height: 80 }],
      [R1, { x: 0, y: 300, width: 100, height: 50 }],
    ])
    const moved = s.model.lineOf(line, (id) => frames.get(id) ?? s.model.storedFrame(id))
    expect(moved.start.x).toBeGreaterThan(500)
    expect(moved.start.x).toBeLessThan(700)
    // The stored row moves too: the line follows without being written.
    s.editor.move(new Map([[C1, { x: 500, y: 0 }]]))
    expect(s.model.lineOf(s.row(L1)!).start).toEqual(moved.start)
    expect(s.source.log.value.filter((m) => m.id === L1)).toHaveLength(0)
  })
})

describe('timeline', () => {
  const m = (id: string, kind: 'milestone' | 'gate', due: string | null): MilestoneRow => ({
    id,
    item_id: 'p',
    kind,
    title: id,
    due_date: due,
    status: 'planned',
    sort_order: id,
  })
  const ms = [m('a', 'milestone', '2026-01-01'), m('b', 'gate', '2026-01-11'), m('c', 'milestone', '2026-01-31')]

  it('spaces milestones evenly along a horizontal line', () => {
    const t = timelineLayout(ms, { width: 448, height: 100 }, { orientation: 'horizontal', spacing: 'even' })
    expect(t.axis).toEqual({ x1: 24, y1: 50, x2: 424, y2: 50 })
    expect(t.marks.map((x) => x.x)).toEqual([24, 224, 424])
    expect(t.marks.map((x) => x.kind)).toEqual(['milestone', 'gate', 'milestone'])
  })

  it('spaces them by date, vertically', () => {
    const t = timelineLayout(ms, { width: 200, height: 348 }, { orientation: 'vertical', spacing: 'date' })
    expect(t.marks.map((x) => x.y)).toEqual([24, 124, 324])
    expect(new Set(t.marks.map((x) => x.x))).toEqual(new Set([24]))
  })
})
