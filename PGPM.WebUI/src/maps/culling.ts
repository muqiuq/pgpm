import { boxesOverlap, type Box } from './geometry'

/**
 * Level of detail and viewport culling (docs/03 §13.1, spike S2).
 *
 * - Detail: at LOD_ZOOM and above everything is drawn (cards as HTML in `<foreignObject>`); below it cards are a
 *   coloured rectangle with the title in plain SVG; below FAR_ZOOM (where 14 px text is under 4.5 px) the titles go too,
 *   and texts and labels smaller than FAR_TEXT_SIZE. Less detail comes at once when zooming out; more detail only when
 *   the view has settled (a zoom gesture doesn't switch hundreds of cards back and forth).
 * - Culling: from CULL_MIN_ELEMENTS elements on, only those near the viewport are drawn – the region is the visible
 *   box grown by CULL_MARGIN of its size on every side, computed anew when the view leaves it, or (once the view has
 *   settled) when the zoom changed by more than RECULL_ZOOM. Panning within the region re-renders nothing but the
 *   world group's transform.
 */

export const LOD_ZOOM = 0.45
export const FAR_ZOOM = 0.3
export const FAR_TEXT_SIZE = 24
export const CULL_MIN_ELEMENTS = 300
export const CULL_MARGIN = 0.5
export const RECULL_ZOOM = 1.6
/** The view counts as settled this long after its last change (ms). */
export const SETTLE_MS = 180

export type Detail = 'full' | 'lod' | 'far'
const RANK: Record<Detail, number> = { far: 0, lod: 1, full: 2 }

export function detailAt(k: number, lodZoom = LOD_ZOOM, farZoom = FAR_ZOOM): Detail {
  return k >= lodZoom ? 'full' : k >= farZoom ? 'lod' : 'far'
}

/** The detail to draw now: less at once, more only once the view has settled. */
export function nextDetail(current: Detail, target: Detail, settled: boolean): Detail {
  return RANK[target] < RANK[current] || settled ? target : current
}

export interface CullState {
  region: Box
  k: number
}

/** The box grown by `margin` × its size on every side. */
export function regionAround(visible: Box, margin = CULL_MARGIN): Box {
  const w = (visible.maxX - visible.minX) * margin
  const h = (visible.maxY - visible.minY) * margin
  return { minX: visible.minX - w, minY: visible.minY - h, maxX: visible.maxX + w, maxY: visible.maxY + h }
}

export function covers(region: Box, visible: Box): boolean {
  return (
    region.minX <= visible.minX &&
    region.minY <= visible.minY &&
    region.maxX >= visible.maxX &&
    region.maxY >= visible.maxY
  )
}

/**
 * The culling region to use now: the old one while it still covers the view (and, once settled, at about the same
 * zoom), else a new one.
 */
export function nextCull(previous: CullState | null, visible: Box, k: number, settled = true): CullState {
  if (previous && covers(previous.region, visible)) {
    if (!settled) return previous
    const ratio = k / previous.k
    if (ratio < RECULL_ZOOM && ratio > 1 / RECULL_ZOOM) return previous
  }
  return { region: regionAround(visible), k }
}

/** The things whose box touches the region, in their order. */
export function cull<T>(things: readonly T[], boxOf: (thing: T) => Box, region: Box): T[] {
  return things.filter((t) => boxesOverlap(boxOf(t), region))
}
