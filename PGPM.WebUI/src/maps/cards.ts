import { evaluateScore, scoreMax, DEFAULT_RATING_MAX, type FieldDef } from '@/domain/score'
import type {
  DodItemRow,
  FieldDefinitionRow,
  GateDecision,
  ItemRow,
  ItemType,
  MilestoneRow,
  MilestoneStatus,
  StatusRow,
} from './types'

/**
 * What an item card shows (doc 01 §12.3, docs/03 §13.1): type icon, key, title, color, cover image, status with the
 * *overdue* marker, persons, milestone progress and the map's configured badges (`maps.settings.cardBadges`). Every
 * card of an item shows the same model, built from the item's rows; pure functions.
 */

export const TYPE_ICONS: Readonly<Record<ItemType, string>> = {
  note: 'pi pi-file-edit',
  idea: 'pi pi-lightbulb',
  lead: 'pi pi-flag',
  project: 'pi pi-briefcase',
}

export const TYPE_LABELS: Readonly<Record<ItemType, string>> = {
  note: 'Note',
  idea: 'Idea',
  lead: 'Lead',
  project: 'Project',
}

const KEY_PREFIX: Readonly<Record<ItemType, string>> = { note: 'N', idea: 'I', lead: 'L', project: 'P' }

/** The display key (data model §6): `I-42`; `I-…` until the server assigned the number. */
export function itemKey(item: Pick<ItemRow, 'type' | 'number'>): string {
  return `${KEY_PREFIX[item.type]}-${item.number ?? '…'}`
}

/** The item colour palette: the note colours of the design system (base.css), shaded per theme. */
export const NOTE_COLORS = ['yellow', 'green', 'blue', 'pink', 'purple', 'grey'] as const

export const TYPE_COLORS: Readonly<Record<ItemType, string>> = {
  note: 'yellow',
  idea: 'purple',
  lead: 'green',
  project: 'blue',
}

const HEX = /^#[0-9a-f]{6}$/i

/** A stored colour as CSS: a palette name → its variable, `#rrggbb` as it is; anything else: null. */
export function cssColor(color: string | null | undefined): string | null {
  if (!color) return null
  if ((NOTE_COLORS as readonly string[]).includes(color)) return `var(--pgpm-note-${color})`
  return HEX.test(color) ? color : null
}

/** The item's colour as CSS: its own, else its type's. */
export function itemColor(item: Pick<ItemRow, 'type' | 'color'>): string {
  return cssColor(item.color) ?? `var(--pgpm-note-${TYPE_COLORS[item.type]})`
}

/**
 * A card's background: palette colours are soft already (and shaded per theme); a custom `#rrggbb` is mixed into the
 * panel colour, so that the text stays readable – the colour itself is the card's accent.
 */
export function cardFill(item: Pick<ItemRow, 'type' | 'color'>): string {
  const own = cssColor(item.color)
  if (own && HEX.test(own)) return `color-mix(in srgb, ${own} 28%, var(--pgpm-panel))`
  return own ?? `var(--pgpm-note-${TYPE_COLORS[item.type]})`
}

/** Readable text on a `#rrggbb` fill (palette colours are light in light mode and dark in dark mode: the theme's text). */
export function textOn(fill: string | null | undefined): string | null {
  if (!fill || !HEX.test(fill)) return null
  const n = parseInt(fill.slice(1), 16)
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255]
  return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? '#0f172a' : '#ffffff'
}

export interface CardBadge {
  key: string
  label: string
  text: string
}

export interface CardStatus {
  name: string
  color: string
  category: StatusRow['category']
}

export interface CardModel {
  itemId: string
  type: ItemType
  icon: string
  key: string
  title: string
  /** CSS colour: the accent (avatar, LOD rectangle, the card's left edge). */
  color: string
  /** CSS colour of the card's background. */
  fill: string
  coverFileId: string | null
  description: string | null
  status: CardStatus | null
  /** A closed status: the card is drawn faded. */
  closed: boolean
  overdue: boolean
  persons: number
  /** Projects: milestones done of those not cancelled. */
  progress: { done: number; total: number } | null
  badges: CardBadge[]
}

/** A field definition as cards and matrices need it. */
export type FieldDefinition = FieldDef & Pick<FieldDefinitionRow, 'id' | 'label'>

export interface CardContext {
  statusesById: ReadonlyMap<string, StatusRow>
  defsByKey: Readonly<Record<string, FieldDefinition>>
  /** An item's field values by field key. */
  valuesByItem: ReadonlyMap<string, Readonly<Record<string, unknown>>>
  personsByItem: ReadonlyMap<string, number>
  milestonesByProject: ReadonlyMap<string, readonly MilestoneRow[]>
  /** `maps.settings.cardBadges`. */
  badgeKeys: readonly string[]
  now: number
}

const DAY = 86_400_000

function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/** Today as `YYYY-MM-DD` in the browser's zone (dates compare as text). */
export function today(now: number): string {
  const d = new Date(now)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/**
 * Overdue (doc 01 §5, docs/03 §12.7): longer than its status' max. days in it, or a timebox date field passed while the
 * item isn't closed.
 */
export function isOverdue(
  item: Pick<ItemRow, 'status_changed_at'>,
  status: StatusRow | undefined,
  defsByKey: Readonly<Record<string, FieldDefinition>>,
  values: Readonly<Record<string, unknown>>,
  now: number,
): boolean {
  if (status && isNumber(status.max_days) && item.status_changed_at) {
    const since = Date.parse(item.status_changed_at)
    if (Number.isFinite(since) && now - since > status.max_days * DAY) return true
  }
  if (status?.category === 'closed') return false
  const day = today(now)
  for (const [key, value] of Object.entries(values)) {
    const def = defsByKey[key]
    if (!def || (def.type !== 'date' && def.type !== 'datetime') || def.config?.timebox !== true) continue
    if (typeof value === 'string' && value.slice(0, 10) < day) return true
  }
  return false
}

/** A field's numeric meaning: numbers, ratings (n/a: none), select points, scores (score.ts). Null: no value. */
export function numericValue(
  def: FieldDef,
  defsByKey: Readonly<Record<string, FieldDef>>,
  values: Readonly<Record<string, unknown>>,
): number | null {
  if (def.type === 'score') return evaluateScore(def, defsByKey, values)
  const value = Object.hasOwn(values, def.key) ? values[def.key] : undefined
  if (def.type === 'number' || def.type === 'rating') return isNumber(value) ? value : null
  if (def.type === 'select') {
    const points = def.config?.options?.find((o) => o.id === value)?.points
    return isNumber(points) ? points : null
  }
  return null
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : String(Math.round(value * 100) / 100)
}

/** A badge's text: "58 / 65" for a score with a maximum and a rating, a number with its unit, an option's label. */
export function badgeText(
  def: FieldDefinition,
  defsByKey: Readonly<Record<string, FieldDef>>,
  values: Readonly<Record<string, unknown>>,
): string {
  const value = numericValue(def, defsByKey, values)
  if (def.type === 'select') {
    const raw = values[def.key]
    return def.config?.options?.find((o) => o.id === raw)?.label ?? '–'
  }
  if (value === null) return '–'
  if (def.type === 'score') {
    const max = scoreMax(def)
    return max === null ? formatNumber(value) : `${formatNumber(value)} / ${formatNumber(max)}`
  }
  if (def.type === 'rating') return `${formatNumber(value)} / ${def.config?.max ?? DEFAULT_RATING_MAX}`
  const unit = typeof def.config?.unit === 'string' ? ` ${def.config.unit}` : ''
  return `${formatNumber(value)}${unit}`
}

/** A project's milestones: done of those not cancelled; null without milestones. */
export function milestoneProgress(milestones: readonly MilestoneRow[]): { done: number; total: number } | null {
  const counted = milestones.filter((m) => m.status !== 'cancelled')
  if (!counted.length) return null
  return { done: counted.filter((m) => m.status === 'done').length, total: counted.length }
}

/** A milestone's Definition of Done (doc 01 §11): done ÷ (all − n/a). */
export function dodProgress(items: readonly DodItemRow[]): { done: number; total: number } {
  const counted = items.filter((d) => d.state !== 'na')
  return { done: counted.filter((d) => d.state === 'done').length, total: counted.length }
}

const NO_VALUES: Readonly<Record<string, unknown>> = Object.freeze({})

export function cardModel(item: ItemRow, ctx: CardContext): CardModel {
  const status = item.status_id ? ctx.statusesById.get(item.status_id) : undefined
  const values = ctx.valuesByItem.get(item.id) ?? NO_VALUES
  const badges: CardBadge[] = []
  for (const key of ctx.badgeKeys) {
    const def = ctx.defsByKey[key]
    if (!def || (item.type !== 'idea' && item.type !== 'project')) continue
    badges.push({ key, label: def.label, text: badgeText(def, ctx.defsByKey, values) })
  }
  return {
    itemId: item.id,
    type: item.type,
    icon: TYPE_ICONS[item.type],
    key: itemKey(item),
    title: item.title,
    color: itemColor(item),
    fill: cardFill(item),
    coverFileId: item.image_file_id ?? null,
    description: item.description ?? null,
    status: status ? { name: status.name, color: status.color, category: status.category } : null,
    closed: status?.category === 'closed',
    overdue: isOverdue(item, status, ctx.defsByKey, values, ctx.now),
    persons: ctx.personsByItem.get(item.id) ?? 0,
    progress: item.type === 'project' ? milestoneProgress(ctx.milestonesByProject.get(item.id) ?? []) : null,
    badges,
  }
}

/** How a milestone or gate is coloured (doc 01 §12.3): by its status, a gate by its decision. */
export type MilestoneTone =
  'planned' | 'active' | 'done' | 'cancelled' | 'pending' | 'go' | 'no_go' | 'iterate' | 'pivot' | 'park'

export function milestoneTone(m: Pick<MilestoneRow, 'kind' | 'status' | 'decision'>): MilestoneTone {
  if (m.kind === 'gate' && m.decision) return m.decision
  const byStatus: Record<MilestoneStatus, MilestoneTone> = {
    planned: 'planned',
    in_progress: 'active',
    done: 'done',
    cancelled: 'cancelled',
  }
  return byStatus[m.status] ?? 'planned'
}

export const TONE_COLORS: Readonly<Record<MilestoneTone, string>> = {
  planned: 'var(--pgpm-none)',
  active: 'var(--p-primary-color)',
  done: 'var(--pgpm-ok)',
  cancelled: 'var(--pgpm-muted)',
  pending: 'var(--pgpm-warn)',
  go: 'var(--pgpm-ok)',
  no_go: 'var(--pgpm-fail)',
  iterate: 'var(--p-primary-color)',
  pivot: 'var(--p-purple-500)',
  park: 'var(--pgpm-none)',
}

export const DECISION_LABELS: Readonly<Record<GateDecision, string>> = {
  pending: 'Pending',
  go: 'Go',
  no_go: 'No-go',
  iterate: 'Iterate',
  pivot: 'Pivot',
  park: 'Park',
}

export const MILESTONE_STATUS_LABELS: Readonly<Record<MilestoneStatus, string>> = {
  planned: 'Planned',
  in_progress: 'In progress',
  done: 'Done',
  cancelled: 'Cancelled',
}

/** A milestone past its due date and neither done nor cancelled. */
export function milestoneOverdue(m: Pick<MilestoneRow, 'due_date' | 'status'>, now: number): boolean {
  return !!m.due_date && m.status !== 'done' && m.status !== 'cancelled' && m.due_date < today(now)
}

const dayFormat = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' })

/** A date (`YYYY-MM-DD`, the day itself – no zone) for display. */
export function formatDay(value: string | null | undefined): string {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})/.exec(value) : null
  if (!match) return '–'
  return dayFormat.format(new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])))
}
