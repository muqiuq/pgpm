import type { Point } from './geometry'
import type { ItemType } from './types'

/** The toolbar's tools (doc 01 §12.4) and what the canvas asks its host for when a tool places something. */

export type Tool =
  | 'select'
  | 'hand'
  | 'note'
  | 'idea'
  | 'lead'
  | 'project'
  | 'milestone'
  | 'timeline'
  | 'rect'
  | 'ellipse'
  | 'line'
  | 'text'
  | 'image'

export interface ToolInfo {
  tool: Tool
  label: string
  icon: string
}

export const TOOL_INFO: readonly ToolInfo[] = [
  { tool: 'select', label: 'Select', icon: 'pi pi-arrow-up-left' },
  { tool: 'hand', label: 'Hand (pan)', icon: 'pi pi-arrows-alt' },
  { tool: 'note', label: 'Note', icon: 'pi pi-file-edit' },
  { tool: 'idea', label: 'Idea', icon: 'pi pi-lightbulb' },
  { tool: 'lead', label: 'Lead', icon: 'pi pi-flag' },
  { tool: 'project', label: 'Project', icon: 'pi pi-briefcase' },
  { tool: 'milestone', label: 'Milestone or gate', icon: 'pi pi-bullseye' },
  { tool: 'timeline', label: 'Milestone timeline', icon: 'pi pi-sliders-h' },
  { tool: 'rect', label: 'Rectangle', icon: 'pi pi-stop' },
  { tool: 'ellipse', label: 'Ellipse', icon: 'pi pi-circle' },
  { tool: 'line', label: 'Line / arrow', icon: 'pi pi-arrow-up-right' },
  { tool: 'text', label: 'Text', icon: 'pi pi-align-left' },
  { tool: 'image', label: 'Image', icon: 'pi pi-image' },
]

export const CANVAS_TOOLS: readonly Tool[] = TOOL_INFO.map((t) => t.tool)
/** On a matrix map the items place themselves: free elements only (doc 01 §12.8). */
export const MATRIX_TOOLS: readonly Tool[] = ['select', 'hand', 'rect', 'ellipse', 'line', 'text', 'image']

export function isItemTool(tool: Tool): tool is ItemType {
  return tool === 'note' || tool === 'idea' || tool === 'lead' || tool === 'project'
}

/** A tool that places or draws something (not Select or Hand). */
export function isPlacingTool(tool: Tool): boolean {
  return tool !== 'select' && tool !== 'hand'
}

/**
 * What a placing tool asks the host for (`requestItem`, `requestMilestone`, `requestProject`, `requestImage`): the
 * host offers *Create new* / *Add existing* (or a picker, an upload) and calls `place` with the chosen id – the canvas
 * then adds the element where the user clicked – or `cancel`.
 */
export interface PlaceRequest<Extra = object> {
  at: Point
  place: (id: string, options?: { width?: number; height?: number }) => void
  cancel: () => void
  /** Items already on the map (Add existing marks them). */
  onMap: ReadonlySet<string>
  extra: Extra
}

export type ItemRequest = PlaceRequest<{ type: ItemType }>
/** An image to store: picked by the host, or pasted (`file`). */
export type ImageRequest = PlaceRequest<{ file: File | null }>

/** Default sizes of new elements. */
export const SIZES = {
  card: { width: 220, height: 92 },
  cardExpanded: { width: 260, height: 220 },
  milestone: { width: 180, height: 36 },
  timeline: { width: 520, height: 110 },
  rect: { width: 180, height: 110 },
  ellipse: { width: 160, height: 110 },
  text: { width: 200, height: 40 },
  image: { width: 240, height: 160 },
  line: { width: 160, height: 0 },
} as const
