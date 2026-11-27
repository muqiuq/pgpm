import type { CardModel } from './cards'
import type { ItemRow, MilestoneRow } from './types'

/** Types the map's components share. */

/** What the element components look up, one object for all of them (it changes when the rows behind it do). */
export interface SceneLookups {
  cards: ReadonlyMap<string, CardModel>
  items: ReadonlyMap<string, ItemRow>
  milestones: ReadonlyMap<string, MilestoneRow>
  milestonesByProject: ReadonlyMap<string, readonly MilestoneRow[]>
  /** File id → URL. */
  images: ReadonlyMap<string, string | null>
  /** Item id → quadrant label (matrix maps). */
  quadrants: ReadonlyMap<string, string> | null
  now: number
}

/** What the side panel shows: elements, an item of the matrix tray, or the map's properties. */
export type PanelContent = { kind: 'elements'; ids: string[] } | { kind: 'item'; itemId: string } | { kind: 'map' }

/** A choice in the filter bar. */
export interface Option<T = string> {
  value: T
  label: string
}
