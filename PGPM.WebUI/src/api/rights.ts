import type { Schemas } from './client'

/**
 * The rights catalogue of docs/03 §6.2: capabilities only – what a user may *do*; what they *see* is decided by sharing.
 * The areas and levels are the server's enums; their order (a higher level includes the lower ones) comes from the
 * server's catalogue, `GET /api/v1/rights`, which the session store loads with `/auth/me` and keeps for an offline start.
 */
export type Area = Schemas['Area']
export type Level = Schemas['Level']

/** A level in an area. */
export type AreaLevel = readonly [Area, Level]

/**
 * What a route, a drawer entry or a button needs: a level in an area, the admin flag (users, groups and rights are admin
 * only), or any of several levels.
 */
export type Right = AreaLevel | 'admin' | { readonly anyOf: readonly AreaLevel[] }

/** Each area's levels in order, from the server's catalogue; empty until it is loaded. */
export const levelOrder: Partial<Record<Area, readonly Level[]>> = {}

/** The server's catalogue becomes the order the checks use. */
export function setCatalogue(catalogue: readonly { area: Area; levels: readonly { level: Level }[] }[]) {
  for (const key of Object.keys(levelOrder) as Area[]) delete levelOrder[key]
  for (const area of catalogue) levelOrder[area.area] = area.levels.map((l) => l.level)
}

/** Whether a right is met, given how one level in an area is checked. */
export function meets(right: Right, isAdmin: boolean, can: (area: Area, level: Level) => boolean): boolean {
  if (right === 'admin') return isAdmin
  if ('anyOf' in right) return right.anyOf.some(([area, level]) => can(area, level))
  return can(right[0], right[1])
}

/**
 * Whether these rights (the admin flag, or the levels per area) include the level asked for. Without the catalogue only
 * the very level counts – the server decides anyway; this only spares the user dead ends.
 */
export function holds(isAdmin: boolean, rights: Partial<Record<Area, Level>>, area: Area, level: Level): boolean {
  if (isAdmin) return true
  const held = rights[area]
  if (!held || held === 'None' || level === 'None') return false
  const order = levelOrder[area]
  if (!order) return held === level
  return order.includes(level) && order.indexOf(held) >= order.indexOf(level)
}
