import { describe, expect, it } from 'vitest'
import { drawPreview, previewColor, previewScene, previewSrc, type PreviewContext } from './preview'
import { BASE, elements, ITEMS, layer, TOP } from './testFixtures'

describe('the map list’s preview (data model §9 maps.preview)', () => {
  const items = new Map(ITEMS.map((i) => [i.id, i]))

  it('draws the visible layers’ elements in order: cards in their colours, shapes, lines, text bars', () => {
    const scene = previewScene({
      elements: elements(),
      layers: [layer(BASE, 'Base', 'a0'), layer(TOP, 'Top', 'a1', { is_visible: false })],
      items,
      background: 'yellow',
    })
    // The hidden layer's text is left out; cards (two of the idea, the project), rect, ellipse, line.
    expect(scene.shapes.map((s) => s.kind)).toEqual(['rect', 'rect', 'rect', 'ellipse', 'line', 'rect'])
    expect(scene.shapes[0]!.fill).toBe('#ede9fe')
    expect(scene.shapes[4]!.points).toEqual([
      [100, 40],
      [100, 300],
    ])
    expect(scene.background).toBe('#fef3c7')
    expect(scene.box.minX).toBeLessThan(0)
    expect(scene.box.maxX).toBeGreaterThan(1000)
  })

  it('draws a matrix map’s quadrants and computed cards over its world', () => {
    const world = { minX: 0, minY: 0, maxX: 2000, maxY: 1400 }
    const scene = previewScene({
      elements: [],
      layers: [],
      items,
      matrix: {
        world,
        quadrants: [{ minX: 100, minY: 100, maxX: 1000, maxY: 700 }],
        cards: [{ id: ITEMS[0]!.id, x: 10, y: 20, width: 180, height: 56 }],
      },
    })
    expect(scene.box).toEqual(world)
    expect(scene.shapes.map((s) => [s.kind, s.width])).toEqual([
      ['rect', 900],
      ['rect', 180],
    ])
  })

  it('fits the scene into the picture and paints each shape', () => {
    const calls: string[] = []
    const ctx = new Proxy({} as Record<string, unknown>, {
      get: (target, key: string) =>
        key in target ? target[key] : (...args: unknown[]) => calls.push(`${key}(${args.length})`),
      set: (target, key: string, value) => {
        target[key] = value
        return true
      },
    }) as unknown as PreviewContext
    drawPreview(ctx, previewScene({ elements: elements(), layers: [layer(BASE, 'Base', 'a0')], items }), 320, 200)
    expect(calls[0]).toBe('fillRect(4)')
    expect(calls.filter((c) => c.startsWith('ellipse'))).toHaveLength(1)
    expect(calls.filter((c) => c.startsWith('lineTo'))).toHaveLength(1)
    expect(calls.at(-1)).toBe('restore(0)')
  })

  it('resolves colours, and the image type of a stored preview', () => {
    expect(previewColor('#123456', null)).toBe('#123456')
    expect(previewColor('var(--x)', '#fff')).toBe('#fff')
    expect(previewSrc('UklGRiQ')).toBe('data:image/webp;base64,UklGRiQ')
    expect(previewSrc('iVBORw0')).toBe('data:image/png;base64,iVBORw0')
    expect(previewSrc(null)).toBeNull()
  })
})
