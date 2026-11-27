/**
 * The map's keyboard (doc 01 §12.2, §12.6, docs/03 §13.4): Delete, Ctrl/⌘ Z and Shift Z (and Ctrl Y), C / V / D,
 * ] / [ with Shift for front/back, A selects all, F fits, + / − zoom, Escape. Arrow keys nudge and Space pans; the
 * canvas handles those itself. Keys typed into a field are not the map's.
 */

export type MapCommand =
  | 'delete'
  | 'undo'
  | 'redo'
  | 'copy'
  | 'cut'
  | 'paste'
  | 'duplicate'
  | 'forward'
  | 'toFront'
  | 'backward'
  | 'toBack'
  | 'selectAll'
  | 'fit'
  | 'zoomIn'
  | 'zoomOut'
  | 'escape'

export interface KeyLike {
  key: string
  code?: string
  ctrlKey?: boolean
  metaKey?: boolean
  shiftKey?: boolean
  altKey?: boolean
  target?: EventTarget | null
}

/** A key typed into a text field, a select or anything editable: not a map command. */
export function isTyping(target: EventTarget | null | undefined): boolean {
  const el = target as HTMLElement | null | undefined
  if (!el || typeof el.closest !== 'function') return false
  return !!el.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]')
}

export function commandFor(e: KeyLike): MapCommand | null {
  if (isTyping(e.target)) return null
  const mod = !!(e.ctrlKey || e.metaKey)
  const key = e.key.length === 1 ? e.key.toLowerCase() : e.key
  // ] and [ by their keys' position as well: on many layouts they need Alt, or Shift turns them into } and {.
  const bracket =
    e.code === 'BracketRight' || key === ']' || key === '}'
      ? ']'
      : e.code === 'BracketLeft' || key === '[' || key === '{'
        ? '['
        : null
  if (mod) {
    if (key === 'z') return e.shiftKey ? 'redo' : 'undo'
    if (key === 'y') return 'redo'
    if (key === 'c') return 'copy'
    if (key === 'x') return 'cut'
    if (key === 'v') return 'paste'
    if (key === 'd') return 'duplicate'
    if (key === 'a') return 'selectAll'
    if (bracket === ']') return e.shiftKey ? 'toFront' : 'forward'
    if (bracket === '[') return e.shiftKey ? 'toBack' : 'backward'
    return null
  }
  if (e.altKey) return null
  if (key === 'Delete' || key === 'Backspace') return 'delete'
  if (key === 'Escape') return 'escape'
  if (key === 'f' && !e.shiftKey) return 'fit'
  if (key === '+' || key === '=') return 'zoomIn'
  if (key === '-') return 'zoomOut'
  return null
}
