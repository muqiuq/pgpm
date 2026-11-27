import type { FieldConfig, FieldType } from '@/domain/score'
import type { View } from './panZoom'
import type { MatrixConfig, Quadrant } from './matrixLayout'

/**
 * The rows a map works with (data model §6–§9, §13), as the client stores them: snake_case columns, jsonb members as
 * nested objects. Only the columns the map UI reads are typed; a row may carry more (synced-row columns, other
 * columns), which the map keeps as they are.
 */

export type ItemType = 'note' | 'idea' | 'lead' | 'project'
export type Access = 'read' | 'edit' | 'manage'
export type ElementKind = 'item' | 'milestone' | 'timeline' | 'rect' | 'ellipse' | 'line' | 'text' | 'image'

export const ITEM_TYPES: readonly ItemType[] = ['note', 'idea', 'lead', 'project']

/** `maps.settings`. */
export interface MapSettings {
  background?: string | null
  grid?: boolean
  snap?: boolean
  gridSize?: number
  /** Field keys whose values the item cards show as badges (scores, ratings, numbers). */
  cardBadges?: string[]
}

/** `maps.matrix`: the layout's configuration and which items appear. */
export interface MatrixSettings extends MatrixConfig {
  filter?: {
    types?: ItemType[] | null
    statusIds?: string[] | null
    templateIds?: string[] | null
    /** Shared with these groups: applied by the data source (it knows the shares). */
    groupIds?: string[] | null
  } | null
}

export interface MapRow {
  id: string
  kind: 'canvas' | 'matrix'
  name: string
  description?: string | null
  owner_user_id?: string | null
  settings?: MapSettings | null
  matrix?: MatrixSettings | null
  default_view?: MapFilter | null
  is_template?: boolean
  template_source_id?: string | null
  access?: Access
  [column: string]: unknown
}

export interface LayerRow {
  id: string
  map_id: string
  name: string
  /** Fractional key: higher is on top. */
  sort_order: string
  is_visible: boolean
  is_locked: boolean
  [column: string]: unknown
}

export type StrokeStyle = 'solid' | 'dashed' | 'dotted'
export type ArrowHead = 'none' | 'arrow' | 'triangle' | 'dot'
export type TextAlign = 'left' | 'center' | 'right'
export type LineType = 'straight' | 'elbow'

/** `map_elements.style` (data model §9), every key optional. */
export interface ElementStyle {
  fill?: string | null
  stroke?: string | null
  strokeWidth?: number
  strokeStyle?: StrokeStyle
  opacity?: number
  radius?: number
  fontSize?: number
  bold?: boolean
  italic?: boolean
  textColor?: string | null
  align?: TextAlign
  arrowStart?: ArrowHead
  arrowEnd?: ArrowHead
  lineType?: LineType
}

/** `map_elements.config` by kind. */
export interface ElementConfig {
  /** item */
  size?: 'compact' | 'expanded'
  /** timeline */
  orientation?: 'horizontal' | 'vertical'
  spacing?: 'even' | 'date'
  milestoneIds?: string[] | null
  showDates?: boolean
  showLabels?: boolean
  [member: string]: unknown
}

export type LinePoints = [number, number][]

export interface ElementRow {
  id: string
  map_id: string
  layer_id: string
  kind: ElementKind
  /** Fractional key within the layer. */
  z: string
  x: number
  y: number
  width: number
  height: number
  item_id?: string | null
  milestone_id?: string | null
  project_id?: string | null
  file_id?: string | null
  text?: string | null
  /** line: `[[x1,y1],[x2,y2]]` relative to x, y (the fallback for connected ends). */
  points?: LinePoints | null
  start_element_id?: string | null
  end_element_id?: string | null
  style?: ElementStyle | null
  config?: ElementConfig | null
  [column: string]: unknown
}

export interface ItemRow {
  id: string
  type: ItemType
  number?: number | null
  title: string
  description?: string | null
  color?: string | null
  image_file_id?: string | null
  status_id?: string | null
  status_changed_at?: string | null
  project_template_id?: string | null
  access?: Access
  [column: string]: unknown
}

export interface StatusRow {
  id: string
  item_type: ItemType
  name: string
  color: string
  icon?: string | null
  category: 'open' | 'active' | 'closed'
  sort_order: number
  max_days?: number | null
  is_archived?: boolean
  [column: string]: unknown
}

export type MilestoneStatus = 'planned' | 'in_progress' | 'done' | 'cancelled'
export type GateDecision = 'pending' | 'go' | 'no_go' | 'iterate' | 'pivot' | 'park'

export interface MilestoneRow {
  id: string
  item_id: string
  kind: 'milestone' | 'gate'
  title: string
  description?: string | null
  due_date?: string | null
  status: MilestoneStatus
  completed_at?: string | null
  decision?: GateDecision | null
  decision_note?: string | null
  color?: string | null
  sort_order: string
  [column: string]: unknown
}

export interface DodItemRow {
  id: string
  milestone_id: string
  text: string
  state: 'open' | 'done' | 'na'
  note?: string | null
  sort_order: string
  [column: string]: unknown
}

export type LinkRelation = 'contributes_to' | 'learning_of' | 'depends_on' | 'related'

export interface LinkRow {
  id: string
  from_item_id: string
  to_item_id: string
  relation: LinkRelation
  [column: string]: unknown
}

export interface AssignmentRow {
  id: string
  item_id?: string | null
  milestone_id?: string | null
  person_id: string
  role?: string | null
  [column: string]: unknown
}

export interface PersonRow {
  id: string
  display_name: string
  [column: string]: unknown
}

export interface FieldDefinitionRow {
  id: string
  key: string
  label: string
  type: FieldType
  config?: FieldConfig | null
  is_archived?: boolean
  [column: string]: unknown
}

export interface FieldValueRow {
  id: string
  item_id: string
  field_id: string
  value: unknown
  [column: string]: unknown
}

/** What the visibility filter can hide by type (doc 01 §12.7): the item types, milestones (and timelines), drawings. */
export type FilterType = ItemType | 'milestone' | 'drawing'
export const FILTER_TYPES: readonly FilterType[] = ['note', 'idea', 'lead', 'project', 'milestone', 'drawing']

/** A score (or number, rating) range: items without a value in it are hidden. */
export interface ScoreRange {
  field: string
  min?: number | null
  max?: number | null
}

/** The visibility filter (doc 01 §12.7); null or a missing member: no restriction. Also `maps.default_view`. */
export interface MapFilter {
  types?: FilterType[] | null
  statusIds?: string[] | null
  personIds?: string[] | null
  colors?: string[] | null
  templateIds?: string[] | null
  quadrants?: Quadrant[] | null
  score?: ScoreRange | null
}

/** `user_settings` `map:<id>` (data model §13): the personal view of a map. */
export interface MapViewSettings extends MapFilter {
  hiddenLayerIds?: string[]
  showLinks?: boolean
  showMinimap?: boolean
  viewport?: View | null
}
