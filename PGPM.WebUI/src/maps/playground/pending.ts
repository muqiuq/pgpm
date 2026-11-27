import type { ItemRequest, PlaceRequest } from '../tools'

/** A placing tool's request the playground's picker answers. */
export type Pending =
  | { kind: 'item'; request: ItemRequest }
  | { kind: 'milestone'; request: PlaceRequest }
  | { kind: 'project'; request: PlaceRequest }
