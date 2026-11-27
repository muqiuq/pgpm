import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { snapPoint, type Point } from './geometry'
import { useMoving, type MovingOptions } from './moving'

/** A pointer event as the map gets one: at a point, on an element that can capture it. */
function pointer(x: number, y: number): PointerEvent {
  return {
    clientX: x,
    clientY: y,
    pointerId: 1,
    currentTarget: { setPointerCapture: () => undefined },
    stopPropagation: () => undefined,
  } as unknown as PointerEvent
}

function setup(extra: Partial<MovingOptions> = {}) {
  const stored = new Map<string, Point>([
    ['a', { x: 0, y: 0 }],
    ['b', { x: 100, y: 0 }],
  ])
  const moved = ref(new Map<string, Point>())
  const at = (id: string) => moved.value.get(id) ?? stored.get(id)!
  const moving = useMoving(moved, { at, toWorld: (e) => ({ x: e.clientX, y: e.clientY }), ...extra })
  return { moved, moving }
}

describe('useMoving (docs/03 §13.2, from PNMTD)', () => {
  it('drags one element, or the selection together, and reports what it moved', () => {
    const { moved, moving } = setup()
    moving.begin(pointer(0, 0), ['a'])
    expect(moving.isDragging()).toBe(true)
    moving.move(pointer(30, 40))
    expect(moving.release()).toEqual({ click: false, ids: ['a'] })
    expect(moving.isDragging()).toBe(false)
    expect(moved.value.get('a')).toEqual({ x: 30, y: 40 })

    moving.begin(pointer(0, 0), ['a', 'b'])
    moving.move(pointer(10, 0))
    moving.end()
    expect(moved.value.get('a')).toEqual({ x: 40, y: 40 })
    expect(moved.value.get('b')).toEqual({ x: 110, y: 0 })
  })

  it('takes a press without a move for a click, and moves nothing', () => {
    const { moved, moving } = setup()
    moving.begin(pointer(5, 5), ['a'])
    moving.move(pointer(6, 6))
    expect(moving.release()).toEqual({ click: true, ids: [] })
    expect(moved.value.size).toBe(0)
    expect(moving.release()).toEqual({ click: false, ids: [] })
  })

  it('puts a drag back on Escape, with the moves from before', () => {
    const { moved, moving } = setup()
    moving.place(new Map([['a', { x: 5, y: 5 }]]))
    moving.begin(pointer(0, 0), ['a', 'b'])
    moving.move(pointer(50, 50))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }))
    expect(moved.value.get('b')).toEqual({ x: 150, y: 50 })
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    expect(moved.value.get('a')).toEqual({ x: 5, y: 5 })
    expect(moved.value.has('b')).toBe(false)
    expect(moving.release()).toEqual({ click: false, ids: [] })
  })

  it('moves a focused selection with the arrow keys, further with Shift', () => {
    const { moved, moving } = setup()
    expect(moving.nudge(new KeyboardEvent('keydown', { key: 'ArrowRight' }), ['a'])).toEqual(['a'])
    expect(moved.value.get('a')).toEqual({ x: 8, y: 0 })
    moving.nudge(new KeyboardEvent('keydown', { key: 'ArrowUp', shiftKey: true }), ['a', 'b'])
    expect(moved.value.get('a')).toEqual({ x: 8, y: -40 })
    expect(moved.value.get('b')).toEqual({ x: 100, y: -40 })
    expect(moving.nudge(new KeyboardEvent('keydown', { key: 'Enter' }), ['a'])).toEqual([])
    expect(moving.nudge(new KeyboardEvent('keydown', { key: 'ArrowDown' }), [])).toEqual([])
    expect(moved.value.get('a')).toEqual({ x: 8, y: -40 })
  })

  it('snaps the first dragged element to the grid, the others keep their distance', () => {
    const { moved, moving } = setup({ snap: (p) => snapPoint(p, 20) })
    moving.place(new Map([['b', { x: 103, y: 7 }]]))
    moving.begin(pointer(0, 0), ['a', 'b'])
    moving.move(pointer(27, 12))
    expect(moved.value.get('a')).toEqual({ x: 20, y: 20 })
    expect(moved.value.get('b')).toEqual({ x: 123, y: 27 })
    moving.move(pointer(33, 12))
    expect(moved.value.get('a')).toEqual({ x: 40, y: 20 })
    moving.end()
  })
})
