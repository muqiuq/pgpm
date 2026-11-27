import { describe, expect, it } from 'vitest'
import { cull, detailAt, nextCull, nextDetail, regionAround } from './culling'
import type { Box } from './geometry'

const view: Box = { minX: 0, minY: 0, maxX: 1000, maxY: 600 }

describe('culling', () => {
  it('grows the visible box by half its size on every side', () => {
    expect(regionAround(view)).toEqual({ minX: -500, minY: -300, maxX: 1500, maxY: 900 })
  })

  it('keeps the region while the view stays inside it at about the same zoom', () => {
    const first = nextCull(null, view, 1)
    const panned = { minX: 300, minY: 200, maxX: 1300, maxY: 800 }
    expect(nextCull(first, panned, 1)).toBe(first)
    expect(nextCull(first, panned, 1.2)).toBe(first)
  })

  it('renews the region when the view leaves it or the zoom changes a lot', () => {
    const first = nextCull(null, view, 1)
    const away = { minX: 1200, minY: 0, maxX: 2200, maxY: 600 }
    expect(nextCull(first, away, 1)).not.toBe(first)
    expect(nextCull(first, away, 1).region.minX).toBe(700)
    // Zoomed in: the view is inside, but the region is far too big.
    expect(nextCull(first, { minX: 400, minY: 250, maxX: 500, maxY: 310 }, 10)).not.toBe(first)
  })

  it('keeps the region during a gesture while it covers the view, whatever the zoom', () => {
    const first = nextCull(null, view, 1)
    expect(nextCull(first, { minX: 400, minY: 250, maxX: 500, maxY: 310 }, 10, false)).toBe(first)
  })

  it('draws less detail at once and more only when settled', () => {
    expect([detailAt(1), detailAt(0.45), detailAt(0.44), detailAt(0.3), detailAt(0.29)]).toEqual([
      'full',
      'full',
      'lod',
      'lod',
      'far',
    ])
    expect(nextDetail('full', 'far', false)).toBe('far')
    expect(nextDetail('far', 'full', false)).toBe('far')
    expect(nextDetail('far', 'full', true)).toBe('full')
  })

  it('keeps the things that touch the region', () => {
    const boxes = [
      { id: 'in', box: { minX: 10, minY: 10, maxX: 20, maxY: 20 } },
      { id: 'edge', box: { minX: 1490, minY: 0, maxX: 1600, maxY: 10 } },
      { id: 'out', box: { minX: 1600, minY: 0, maxX: 1700, maxY: 10 } },
    ]
    expect(cull(boxes, (b) => b.box, regionAround(view)).map((b) => b.id)).toEqual(['in', 'edge'])
  })
})
