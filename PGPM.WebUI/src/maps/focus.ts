/**
 * A deep link to something on a map (docs/03 §13.1, §13.2), PNMTD's components/graph/focus.ts: `?focus=item:<id>`
 * selects all cards of the item and fits the view to them; `element:<id>` one element. A kind of the map's and a UUID,
 * case-insensitive; anything else: no focus.
 */
export function parseFocus<K extends string>(value: unknown, kinds: readonly K[]): { kind: K; id: string } | null {
  if (typeof value !== 'string') return null
  const match = /^([a-z]+):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.exec(value.trim())
  if (!match) return null
  const kind = match[1]!.toLowerCase() as K
  return kinds.includes(kind) ? { kind, id: match[2]!.toLowerCase() } : null
}

/** The query value for a focus. */
export function formatFocus(kind: string, id: string): string {
  return `${kind}:${id.toLowerCase()}`
}
