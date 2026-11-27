/**
 * The rights catalogue of docs/03 §6.2: capabilities only – what a user may *do*; what they *see* is decided by
 * sharing. Phase 1 takes Area and Level from the server's enums (Schemas['Area'], Schemas['Level']) and checks this list
 * against them; the levels are in order, a higher one includes the lower ones.
 */
export const levelOrder = {
  Notes: ['Create'],
  Ideas: ['Create'],
  Leads: ['Create'],
  Projects: ['Create'],
  Maps: ['Create'],
  Persons: ['Edit'],
  Configuration: ['Manage'],
  Directory: ['Manage'],
  Mcp: ['Use', 'View', 'Manage'],
  AuditLog: ['View'],
  System: ['View'],
} as const satisfies Record<string, readonly string[]>

export type Area = keyof typeof levelOrder
export type Level = (typeof levelOrder)[Area][number]

/** A level in an area. */
export type AreaLevel = readonly [Area, Level]

/**
 * What a route, a drawer entry or a button needs: a level in an area, the admin flag (users, groups and rights are admin
 * only), or any of several levels.
 */
export type Right = AreaLevel | 'admin' | { readonly anyOf: readonly AreaLevel[] }

/** Whether a right is met, given how one level in an area is checked. */
export function meets(right: Right, isAdmin: boolean, can: (area: Area, level: Level) => boolean): boolean {
  if (right === 'admin') return isAdmin
  if ('anyOf' in right) return right.anyOf.some(([area, level]) => can(area, level))
  return can(right[0], right[1])
}

/** Whether these rights (the admin flag, or the levels per area) include the level asked for. */
export function holds(isAdmin: boolean, rights: Partial<Record<Area, Level>>, area: Area, level: Level): boolean {
  if (isAdmin) return true
  const order: readonly Level[] = levelOrder[area]
  const held = rights[area]
  return !!held && order.includes(level) && order.indexOf(held) >= order.indexOf(level)
}
