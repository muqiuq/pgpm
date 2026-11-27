import { scoreMax, type FieldConfig } from '@/domain/score'
import { allOptions, isEmptyValue, numberSettings, optionOf, ratingScale, type FieldLike } from './fieldWidget'

/**
 * How a field's value is shown (docs/01 §6.1, docs/03 §12.7; PLM's renderValue): as text for lists, search and
 * read-only places, and as parts for the widgets – select options with their colour and points (a muted suffix), an
 * option id the configuration no longer has as *(removed option)*, ratings as "4 / 5" or *n/a*, scores as "58 / 65",
 * dates with an *overdue* marker when a timebox date has passed.
 */

export const REMOVED_OPTION = '(removed option)'

export interface RenderContext {
  /** A person's name by id; undefined: not on this device. */
  personName?: (id: string) => string | undefined
}

export interface OptionPart {
  id: string
  label: string
  color: string | null
  points: number | null
  /** The id is not among the field's options (any more). */
  removed: boolean
  archived: boolean
}

export interface Rendered {
  /** The value as one line of text ('' when empty). */
  text: string
  empty: boolean
  /** Select and multiselect: the chosen options. */
  options?: OptionPart[]
  /** Rating: *n/a*. */
  na?: boolean
}

/** "12 Nov 2026" for an ISO date. */
export function formatDate(value: string): string {
  const [y, m, d] = value.split('-').map(Number)
  if (!y || !m || !d) return value
  return new Date(y, m - 1, d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

/** A UTC timestamp in the browser's zone: "12 Nov 2026, 14:00". */
export function formatDateTime(value: string): string {
  const time = Date.parse(value)
  if (Number.isNaN(time)) return value
  return new Date(time).toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

/** A number with the field's decimals and unit: "15,000 CHF / year". */
export function formatNumber(config: FieldConfig | null | undefined, value: number): string {
  const { decimals, unit } = numberSettings(config)
  const text = value.toLocaleString(undefined, {
    minimumFractionDigits: decimals ?? 0,
    maximumFractionDigits: decimals ?? 10,
  })
  return unit ? `${text} ${unit}` : text
}

/** A score's result with its maximum: "58 / 65"; "–" while incomplete. */
export function scoreText(value: number | null, max: number | null, decimals = 0): string {
  if (value === null) return '–'
  const text = value.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
  return max === null ? text : `${text} / ${max.toLocaleString()}`
}

export function optionPart(config: FieldConfig | null | undefined, id: string): OptionPart {
  const option = optionOf(config, id)
  if (!option) return { id, label: REMOVED_OPTION, color: null, points: null, removed: true, archived: false }
  return {
    id,
    label: option.label ?? id,
    color: option.color ?? null,
    points: typeof option.points === 'number' ? option.points : null,
    removed: false,
    archived: option.archived === true,
  }
}

/** An option as text: its label and, if it has points, "(+5)". */
export function optionText(part: OptionPart): string {
  return part.points === null ? part.label : `${part.label} (+${part.points})`
}

/**
 * A stored value for display. `score` fields take the computed value (`value` = the result or null); every other type
 * takes `field_values.value`.
 */
export function renderValue(field: FieldLike, value: unknown, ctx: RenderContext = {}): Rendered {
  const empty: Rendered = { text: '', empty: true }
  if (field.type !== 'score' && isEmptyValue(value)) return field.type === 'bool' ? { text: 'No', empty: false } : empty
  const c = field.config ?? {}
  switch (field.type) {
    case 'score': {
      if (typeof value !== 'number') return { text: '', empty: true }
      return {
        text: scoreText(value, scoreMax(field), Number.isInteger(c.decimals) ? (c.decimals as number) : 0),
        empty: false,
      }
    }
    case 'number':
      return typeof value === 'number'
        ? { text: formatNumber(c, value), empty: false }
        : { text: String(value), empty: false }
    case 'date':
      return { text: typeof value === 'string' ? formatDate(value) : String(value), empty: false }
    case 'datetime':
      return { text: typeof value === 'string' ? formatDateTime(value) : String(value), empty: false }
    case 'bool':
      return { text: value === true ? 'Yes' : 'No', empty: false }
    case 'select': {
      const part = optionPart(c, String(value))
      return { text: optionText(part), empty: false, options: [part] }
    }
    case 'multiselect': {
      const ids = Array.isArray(value) ? value.map(String) : [String(value)]
      // In the configured order; removed ones last.
      const order = new Map(allOptions(c).map((o, i) => [o.id, i]))
      const parts = [...new Set(ids)]
        .map((id) => optionPart(c, id))
        .sort((a, b) => (order.get(a.id) ?? Infinity) - (order.get(b.id) ?? Infinity))
      return { text: parts.map(optionText).join(', '), empty: false, options: parts }
    }
    case 'person': {
      const ids = Array.isArray(value) ? value.map(String) : [String(value)]
      return { text: ids.map((id) => ctx.personName?.(id) ?? 'Unknown person').join(', '), empty: false }
    }
    case 'rating': {
      if (value === 'na') return { text: 'n/a', empty: false, na: true }
      const { max, labels } = ratingScale(c)
      const label = labels[String(value)]
      return { text: `${String(value)} / ${max}${label ? ` (${label})` : ''}`, empty: false }
    }
    default:
      return { text: typeof value === 'string' ? value : JSON.stringify(value), empty: false }
  }
}

/** The text a value is found by in a list's search (texts, option labels, person names). */
export function searchText(field: FieldLike, value: unknown, ctx: RenderContext = {}): string {
  if (isEmptyValue(value)) return ''
  switch (field.type) {
    case 'text':
    case 'textarea':
    case 'markdown':
    case 'url':
    case 'email':
      return typeof value === 'string' ? value : ''
    case 'select':
    case 'multiselect':
    case 'person':
      return renderValue(field, value, ctx).text
    default:
      return ''
  }
}

/**
 * The value a list sorts a field's column by: numbers as such (ratings: n/a below 0), dates as ISO text, a select by its
 * position among the options (the configured order), texts as text; empty: null (last).
 */
export function sortValueOf(field: FieldLike, value: unknown, ctx: RenderContext = {}): unknown {
  if (field.type === 'score') return typeof value === 'number' ? value : null
  if (isEmptyValue(value)) return null
  switch (field.type) {
    case 'number':
      return typeof value === 'number' ? value : null
    case 'rating':
      return value === 'na' ? -1 : typeof value === 'number' ? value : null
    case 'bool':
      return value === true
    case 'select': {
      const index = allOptions(field.config).findIndex((o) => o.id === value)
      return index < 0 ? null : index
    }
    case 'multiselect':
      return Array.isArray(value) ? value.length : null
    default:
      return renderValue(field, value, ctx).text || null
  }
}

/** Whether a timebox date (`config.timebox`) has passed while the item isn't closed (docs/01 §6.1, F6). */
export function timeboxPassed(field: FieldLike, value: unknown, today: string, closed: boolean): boolean {
  if (closed || field.config?.timebox !== true || typeof value !== 'string' || !value) return false
  if (field.type === 'date') return value < today
  if (field.type === 'datetime') {
    const time = Date.parse(value)
    return !Number.isNaN(time) && time < Date.now()
  }
  return false
}

/** Why an item's timebox fields make it overdue – the first passed one, in the fields' order – or null. */
export function timeboxReason(
  fields: readonly FieldLike[],
  values: Readonly<Record<string, unknown>>,
  today: string,
  closed: boolean,
): string | null {
  if (closed) return null
  for (const field of fields) {
    const value = values[field.key]
    if (!timeboxPassed(field, value, today, false)) continue
    const when = field.type === 'date' ? formatDate(String(value)) : formatDateTime(String(value))
    return `${field.label} passed (${when})`
  }
  return null
}
