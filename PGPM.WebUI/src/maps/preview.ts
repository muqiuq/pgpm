import { compareKeyThenId } from '@/domain/fractional'
import { toBase64 } from '@/local/images'
import { NOTE_COLORS, TYPE_COLORS } from './cards'
import { boundsOf, padBox, type Box, type Rect } from './geometry'
import type { ElementRow, ItemRow, LayerRow } from './types'

/**
 * `maps.preview` (data model §9): the small picture of a map in the map list, rendered by the client when the user
 * leaves a map they changed. Not a screenshot of the SVG – its cards are `<foreignObject>` HTML and its colours CSS
 * variables, which a canvas can't take (Safari taints it, docs/03 §13.4 *Export*) – but a schematic drawn straight onto
 * a canvas from the rows: cards as coloured boxes, shapes with their fill and stroke, lines, text as grey bars, images
 * as grey boxes, a matrix map's quadrants and computed cards. WebP where the browser encodes it, else PNG.
 */

export const PREVIEW_WIDTH = 320
export const PREVIEW_HEIGHT = 200

/** The palette's colours in light mode (base.css `--pgpm-note-*`): the preview is the same picture in either theme. */
const PALETTE: Readonly<Record<(typeof NOTE_COLORS)[number], string>> = {
  yellow: '#fef3c7',
  green: '#dcfce7',
  blue: '#dbeafe',
  pink: '#fce7f3',
  purple: '#ede9fe',
  grey: '#e5e7eb',
}
const HEX = /^#[0-9a-f]{6}$/i

/** A stored colour (a palette name, `#rrggbb`) as a canvas colour; the fallback for none. */
export function previewColor(color: string | null | undefined, fallback: string | null): string | null {
  if (color && Object.hasOwn(PALETTE, color)) return PALETTE[color as keyof typeof PALETTE]
  return color && HEX.test(color) ? color : fallback
}

export interface PreviewShape {
  kind: 'rect' | 'ellipse' | 'line'
  x: number
  y: number
  width: number
  height: number
  fill: string | null
  stroke: string | null
  /** Lines: absolute points. */
  points?: [number, number][]
}

export interface PreviewScene {
  box: Box
  background: string
  shapes: PreviewShape[]
}

export interface PreviewInput {
  elements: readonly ElementRow[]
  layers: readonly LayerRow[]
  items: ReadonlyMap<string, ItemRow>
  /** `maps.settings.background`. */
  background?: string | null
  /** A matrix map: its world, quadrants and computed cards (matrixLayout.ts). */
  matrix?: { world: Box; quadrants: readonly Box[]; cards: readonly (Rect & { id: string })[] } | null
}

const CARD_STROKE = '#94a3b8'

function cardShape(frame: Rect, item: ItemRow | undefined): PreviewShape {
  const fill = item ? previewColor(item.color, previewColor(TYPE_COLORS[item.type], '#e2e8f0')) : '#e2e8f0'
  return { kind: 'rect', ...frame, fill, stroke: CARD_STROKE }
}

/** What the preview draws, in drawing order, and the box it shows. */
export function previewScene(input: PreviewInput): PreviewScene {
  const visible = [...input.layers]
    .filter((l) => l.is_visible)
    .sort((a, b) => compareKeyThenId(a.sort_order, a.id, b.sort_order, b.id))
  const order = new Map(visible.map((l, i) => [l.id, i]))
  const elements = input.elements
    .filter((e) => order.has(e.layer_id))
    .sort((a, b) => order.get(a.layer_id)! - order.get(b.layer_id)! || compareKeyThenId(a.z, a.id, b.z, b.id))
  const shapes: PreviewShape[] = []
  if (input.matrix) {
    input.matrix.quadrants.forEach((q, i) =>
      shapes.push({
        kind: 'rect',
        x: q.minX,
        y: q.minY,
        width: q.maxX - q.minX,
        height: q.maxY - q.minY,
        fill: i % 3 === 0 ? '#f1f5f9' : '#f8fafc',
        stroke: '#cbd5e1',
      }),
    )
    for (const c of input.matrix.cards) {
      shapes.push(cardShape({ x: c.x, y: c.y, width: c.width, height: c.height }, input.items.get(c.id)))
    }
  }
  for (const e of elements) {
    const frame = { x: e.x, y: e.y, width: e.width, height: e.height }
    const style = e.style ?? {}
    switch (e.kind) {
      case 'item':
        shapes.push(cardShape(frame, e.item_id ? input.items.get(e.item_id) : undefined))
        break
      case 'rect':
      case 'ellipse':
        shapes.push({
          kind: e.kind,
          ...frame,
          fill: previewColor(style.fill, '#f1f5f9'),
          stroke: previewColor(style.stroke, '#64748b'),
        })
        break
      case 'line': {
        const points = (
          e.points?.length
            ? e.points
            : [
                [0, 0],
                [e.width, e.height],
              ]
        ) as [number, number][]
        shapes.push({
          kind: 'line',
          ...frame,
          fill: null,
          stroke: previewColor(style.stroke, '#475569'),
          points: points.map(([x, y]) => [e.x + x, e.y + y]),
        })
        break
      }
      case 'text':
        shapes.push({
          kind: 'rect',
          x: e.x,
          y: e.y + e.height * 0.3,
          width: e.width * 0.8,
          height: Math.max(2, e.height * 0.4),
          fill: previewColor(style.textColor, '#94a3b8'),
          stroke: null,
        })
        break
      case 'image':
        shapes.push({ kind: 'rect', ...frame, fill: '#cbd5e1', stroke: null })
        break
      case 'milestone':
        shapes.push({ kind: 'ellipse', ...frame, width: frame.height, fill: '#a5b4fc', stroke: '#6366f1' })
        break
      case 'timeline':
        shapes.push({
          kind: 'line',
          ...frame,
          fill: null,
          stroke: '#6366f1',
          points: [
            [e.x, e.y + e.height / 2],
            [e.x + e.width, e.y + e.height / 2],
          ],
        })
        break
    }
  }
  const box =
    input.matrix?.world ?? padBox(boundsOf(shapes, { minX: 0, minY: 0, maxX: PREVIEW_WIDTH, maxY: PREVIEW_HEIGHT }), 24)
  return { box, background: previewColor(input.background, '#ffffff')!, shapes }
}

/** The part of the canvas 2D context the drawing uses. */
export type PreviewContext = Pick<
  CanvasRenderingContext2D,
  | 'fillStyle'
  | 'strokeStyle'
  | 'lineWidth'
  | 'fillRect'
  | 'strokeRect'
  | 'beginPath'
  | 'ellipse'
  | 'moveTo'
  | 'lineTo'
  | 'fill'
  | 'stroke'
  | 'save'
  | 'restore'
  | 'translate'
  | 'scale'
>

/** Draws the scene fitted into width × height, centred. */
export function drawPreview(ctx: PreviewContext, scene: PreviewScene, width = PREVIEW_WIDTH, height = PREVIEW_HEIGHT) {
  ctx.fillStyle = scene.background
  ctx.fillRect(0, 0, width, height)
  const w = Math.max(1, scene.box.maxX - scene.box.minX)
  const h = Math.max(1, scene.box.maxY - scene.box.minY)
  const k = Math.min(width / w, height / h)
  ctx.save()
  ctx.translate((width - w * k) / 2, (height - h * k) / 2)
  ctx.scale(k, k)
  ctx.translate(-scene.box.minX, -scene.box.minY)
  ctx.lineWidth = Math.max(1, 1 / k)
  for (const s of scene.shapes) {
    if (s.kind === 'line' && s.points?.length) {
      ctx.beginPath()
      ctx.moveTo(s.points[0]![0], s.points[0]![1])
      for (const [x, y] of s.points.slice(1)) ctx.lineTo(x, y)
      ctx.strokeStyle = s.stroke ?? '#475569'
      ctx.lineWidth = Math.max(1.5, 2 / k)
      ctx.stroke()
      ctx.lineWidth = Math.max(1, 1 / k)
    } else if (s.kind === 'ellipse') {
      ctx.beginPath()
      ctx.ellipse(s.x + s.width / 2, s.y + s.height / 2, s.width / 2, s.height / 2, 0, 0, Math.PI * 2)
      if (s.fill) {
        ctx.fillStyle = s.fill
        ctx.fill()
      }
      if (s.stroke) {
        ctx.strokeStyle = s.stroke
        ctx.stroke()
      }
    } else {
      if (s.fill) {
        ctx.fillStyle = s.fill
        ctx.fillRect(s.x, s.y, s.width, s.height)
      }
      if (s.stroke) {
        ctx.strokeStyle = s.stroke
        ctx.strokeRect(s.x, s.y, s.width, s.height)
      }
    }
  }
  ctx.restore()
}

/** The preview as base64 (WebP, else PNG); null where the browser can't draw one. */
export async function renderPreview(scene: PreviewScene): Promise<string | null> {
  if (typeof document === 'undefined') return null
  const canvas = document.createElement('canvas')
  canvas.width = PREVIEW_WIDTH
  canvas.height = PREVIEW_HEIGHT
  let ctx: CanvasRenderingContext2D | null = null
  try {
    ctx = canvas.getContext('2d')
  } catch {
    return null
  }
  if (!ctx) return null
  drawPreview(ctx, scene)
  const blob = await new Promise<Blob | null>((resolve) => {
    try {
      canvas.toBlob(resolve, 'image/webp', 0.82)
    } catch {
      resolve(null)
    }
  })
  if (!blob || blob.size > 200 * 1024) return null
  return toBase64(new Uint8Array(await blob.arrayBuffer()))
}

/** A stored preview as an `<img>` source: the type from its first bytes. */
export function previewSrc(preview: string | null | undefined): string | null {
  if (!preview) return null
  const type = preview.startsWith('iVBOR') ? 'image/png' : preview.startsWith('/9j/') ? 'image/jpeg' : 'image/webp'
  return `data:${type};base64,${preview}`
}
