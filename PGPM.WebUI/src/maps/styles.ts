import { cssColor } from './cards'
import { dashArray } from './scene'
import type { ElementKind, ElementRow, ElementStyle } from './types'

/**
 * Element styles (data model §9 `map_elements.style`): what a missing key means per kind, and the stored colours as
 * CSS – a palette name (the note colours), `#rrggbb`, or `none` for no paint. Defaults are the theme's colours, so an
 * element nobody styled looks right in light and dark.
 */

export const DEFAULT_STYLES: Readonly<Partial<Record<ElementKind, ElementStyle>>> = {
  rect: { strokeWidth: 1.5, strokeStyle: 'solid', opacity: 1, radius: 8, fontSize: 14, align: 'center' },
  ellipse: { strokeWidth: 1.5, strokeStyle: 'solid', opacity: 1, fontSize: 14, align: 'center' },
  line: {
    strokeWidth: 2,
    strokeStyle: 'solid',
    opacity: 1,
    arrowStart: 'none',
    arrowEnd: 'arrow',
    lineType: 'straight',
    fontSize: 12,
  },
  text: { fontSize: 16, opacity: 1, align: 'left', bold: false, italic: false },
  image: { strokeWidth: 0, opacity: 1, radius: 0 },
}

const DEFAULT_PAINT: Readonly<Partial<Record<ElementKind, { fill: string; stroke: string; text: string }>>> = {
  rect: { fill: 'var(--pgpm-note-blue)', stroke: 'var(--pgpm-muted)', text: 'var(--pgpm-text)' },
  ellipse: { fill: 'var(--pgpm-note-green)', stroke: 'var(--pgpm-muted)', text: 'var(--pgpm-text)' },
  line: { fill: 'transparent', stroke: 'var(--pgpm-text)', text: 'var(--pgpm-text)' },
  text: { fill: 'transparent', stroke: 'transparent', text: 'var(--pgpm-text)' },
  image: { fill: 'transparent', stroke: 'var(--pgpm-border)', text: 'var(--pgpm-text)' },
}

/** A stored paint as CSS: `none`/`transparent` → transparent, a colour → itself, else the fallback. */
export function paint(value: string | null | undefined, fallback: string): string {
  if (value === 'none' || value === 'transparent') return 'transparent'
  return cssColor(value) ?? fallback
}

export interface ResolvedStyle extends Required<Omit<ElementStyle, 'fill' | 'stroke' | 'textColor'>> {
  fill: string
  stroke: string
  textColor: string
  dash: string | undefined
}

/** The element's style with every key: its own, else its kind's default. */
export function resolveStyle(el: Pick<ElementRow, 'kind' | 'style'>): ResolvedStyle {
  const s = { ...DEFAULT_STYLES[el.kind], ...(el.style ?? {}) }
  const p = DEFAULT_PAINT[el.kind] ?? DEFAULT_PAINT.rect!
  const strokeWidth = s.strokeWidth ?? 1.5
  return {
    fill: paint(s.fill, p.fill),
    stroke: paint(s.stroke, p.stroke),
    textColor: paint(s.textColor, p.text),
    strokeWidth,
    strokeStyle: s.strokeStyle ?? 'solid',
    opacity: s.opacity ?? 1,
    radius: s.radius ?? 0,
    fontSize: s.fontSize ?? 14,
    bold: s.bold ?? false,
    italic: s.italic ?? false,
    align: s.align ?? 'left',
    arrowStart: s.arrowStart ?? 'none',
    arrowEnd: s.arrowEnd ?? 'none',
    lineType: s.lineType ?? 'straight',
    dash: dashArray(s.strokeStyle, strokeWidth),
  }
}

/** The style keys the side panel offers per kind (doc 01 §12.5). */
export const STYLE_KEYS: Readonly<Partial<Record<ElementKind, readonly (keyof ElementStyle)[]>>> = {
  rect: [
    'fill',
    'stroke',
    'strokeWidth',
    'strokeStyle',
    'opacity',
    'radius',
    'fontSize',
    'bold',
    'italic',
    'textColor',
    'align',
  ],
  ellipse: [
    'fill',
    'stroke',
    'strokeWidth',
    'strokeStyle',
    'opacity',
    'fontSize',
    'bold',
    'italic',
    'textColor',
    'align',
  ],
  line: [
    'stroke',
    'strokeWidth',
    'strokeStyle',
    'opacity',
    'arrowStart',
    'arrowEnd',
    'lineType',
    'fontSize',
    'textColor',
  ],
  text: ['fill', 'fontSize', 'bold', 'italic', 'textColor', 'align', 'opacity'],
  image: ['stroke', 'strokeWidth', 'radius', 'opacity'],
}

/** The keys all of the kinds offer (a multi-selection's common style). */
export function commonStyleKeys(kinds: Iterable<ElementKind>): (keyof ElementStyle)[] {
  let common: (keyof ElementStyle)[] | null = null
  for (const kind of kinds) {
    const keys = STYLE_KEYS[kind] ?? []
    common = common === null ? [...keys] : common.filter((k) => keys.includes(k))
  }
  return common ?? []
}
