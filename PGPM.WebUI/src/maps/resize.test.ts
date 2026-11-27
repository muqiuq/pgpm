import { describe, expect, it } from 'vitest'
import { handlePoint, MIN_SIZE, resizeFrame } from './resize'

const frame = { x: 100, y: 100, width: 200, height: 100 }

describe('resizeFrame', () => {
  it('moves the handle’s edges and keeps the others', () => {
    expect(resizeFrame(frame, 'se', { x: 350, y: 260 })).toEqual({ x: 100, y: 100, width: 250, height: 160 })
    expect(resizeFrame(frame, 'nw', { x: 50, y: 80 })).toEqual({ x: 50, y: 80, width: 250, height: 120 })
    expect(resizeFrame(frame, 'e', { x: 250, y: 999 })).toEqual({ x: 100, y: 100, width: 150, height: 100 })
    expect(resizeFrame(frame, 'n', { x: 0, y: 150 })).toEqual({ x: 100, y: 150, width: 200, height: 50 })
  })

  it('never flips nor gets smaller than the minimum', () => {
    expect(resizeFrame(frame, 'se', { x: 0, y: 0 })).toEqual({ x: 100, y: 100, width: MIN_SIZE, height: MIN_SIZE })
    expect(resizeFrame(frame, 'w', { x: 900, y: 0 })).toEqual({
      x: 300 - MIN_SIZE,
      y: 100,
      width: MIN_SIZE,
      height: 100,
    })
  })

  it('keeps the aspect ratio with Shift: corners from the opposite corner', () => {
    const r = resizeFrame(frame, 'se', { x: 500, y: 150 }, { keepRatio: true })
    expect(r).toEqual({ x: 100, y: 100, width: 400, height: 200 })
    const nw = resizeFrame(frame, 'nw', { x: 200, y: 0 }, { keepRatio: true })
    // The taller change wins: height 200 → width 400, anchored at the bottom right (300, 200).
    expect(nw).toEqual({ x: -100, y: 0, width: 400, height: 200 })
    expect(nw.width / nw.height).toBe(2)
  })

  it('keeps the ratio from an edge around the middle of the other axis', () => {
    const r = resizeFrame(frame, 'e', { x: 500, y: 0 }, { keepRatio: true })
    expect(r).toEqual({ x: 100, y: 50, width: 400, height: 200 })
    const s = resizeFrame(frame, 's', { x: 0, y: 150 }, { keepRatio: true })
    expect(s).toEqual({ x: 150, y: 100, width: 100, height: 50 })
  })

  it('snaps the moving edges to the grid', () => {
    expect(resizeFrame(frame, 'se', { x: 347, y: 211 }, { grid: 20 })).toEqual({
      x: 100,
      y: 100,
      width: 240,
      height: 120,
    })
  })

  it('places the handles on the frame', () => {
    expect(handlePoint(frame, 'nw')).toEqual({ x: 100, y: 100 })
    expect(handlePoint(frame, 's')).toEqual({ x: 200, y: 200 })
    expect(handlePoint(frame, 'e')).toEqual({ x: 300, y: 150 })
  })
})
