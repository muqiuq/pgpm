import { DEFAULT_RATING_MAX, type FieldConfig, type FieldType, type SelectOption } from '@/domain/score'

/**
 * The field types' editing rules (docs/01 §6.1, data model §7.4–§7.5; PLM's one switch over the types): which widget
 * edits a field, how an editor's draft becomes the stored value (`field_values.value`) and back, and whether a value fits
 * – the same checks as the server's `FieldValueRules` (PGPM/Config/FieldValueRules.cs), so that a value the device
 * accepts offline isn't refused at the sync.
 */

/** A field definition as far as the widgets need it. */
export interface FieldLike {
  key: string
  label: string
  type: FieldType
  config?: FieldConfig | null
  required?: boolean
  helpText?: string | null
  isArchived?: boolean
}

/** How a field is edited on an item's page. */
export interface Widget {
  type: FieldType
  /**
   * The `InlineField` type for the fields edited inline (text, numbers, dates, switches, single choices); `custom` for
   * the ones with their own editor in its slot (multi choices, persons, date-times); null for the rest (markdown: its
   * own page; rating: one click; score: never edited).
   */
  inline: 'text' | 'textarea' | 'number' | 'date' | 'bool' | 'enum' | 'custom' | null
  /** Edited on its own page in the Markdown editor (`/<type>/:id/fields/:key/edit`). */
  page: boolean
  /** Computed, read-only (score). */
  computed: boolean
  /** Edited with one click (rating 0–5 / n/a). */
  oneClick: boolean
  /** The value is a list (multiselect, person). */
  list: boolean
}

export function fieldWidget(field: Pick<FieldLike, 'type'>): Widget {
  const base: Widget = { type: field.type, inline: null, page: false, computed: false, oneClick: false, list: false }
  switch (field.type) {
    case 'text':
    case 'url':
    case 'email':
      return { ...base, inline: 'text' }
    case 'textarea':
      return { ...base, inline: 'textarea' }
    case 'markdown':
      return { ...base, page: true }
    case 'number':
      return { ...base, inline: 'number' }
    case 'date':
      return { ...base, inline: 'date' }
    case 'datetime':
      return { ...base, inline: 'custom' }
    case 'bool':
      return { ...base, inline: 'bool' }
    case 'select':
      return { ...base, inline: 'enum' }
    case 'multiselect':
    case 'person':
      return { ...base, inline: 'custom', list: true }
    case 'rating':
      return { ...base, oneClick: true }
    case 'score':
      return { ...base, computed: true }
  }
}

// --- configuration readers -----------------------------------------------------------------------------------------

/** A rating's scale: `min`–`max` (default 0–5), whether *n/a* is allowed, the labels of some values. */
export function ratingScale(config: FieldConfig | null | undefined): {
  min: number
  max: number
  allowNa: boolean
  labels: Record<string, string>
} {
  const c = config ?? {}
  const min = Number.isInteger(c.min) ? (c.min as number) : 0
  const max = Number.isInteger(c.max) ? (c.max as number) : DEFAULT_RATING_MAX
  const labels = typeof c.labels === 'object' && c.labels !== null ? (c.labels as Record<string, string>) : {}
  return { min, max: Math.max(min, max), allowNa: c.allowNa === true, labels }
}

/** All options of a select (archived ones too), as configured. */
export function allOptions(config: FieldConfig | null | undefined): SelectOption[] {
  return Array.isArray(config?.options) ? config!.options.filter((o) => typeof o?.id === 'string') : []
}

/** The options one may pick: not archived, plus the chosen ones (still shown, still kept). */
export function pickableOptions(
  config: FieldConfig | null | undefined,
  chosen: readonly unknown[] = [],
): SelectOption[] {
  return allOptions(config).filter((o) => !o.archived || chosen.includes(o.id))
}

export function optionOf(config: FieldConfig | null | undefined, id: unknown): SelectOption | undefined {
  return typeof id === 'string' ? allOptions(config).find((o) => o.id === id) : undefined
}

/** A number field's settings: decimals (default as typed), min, max, unit. */
export function numberSettings(config: FieldConfig | null | undefined): {
  decimals: number | null
  min: number | null
  max: number | null
  unit: string | null
} {
  const c = config ?? {}
  const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null)
  return {
    decimals: Number.isInteger(c.decimals) ? (c.decimals as number) : null,
    min: num(c.min),
    max: num(c.max),
    unit: typeof c.unit === 'string' && c.unit ? c.unit : null,
  }
}

/** The optional sub-heading a field starts (docs/03 §17: the four feasibility areas). */
export function subheadingOf(field: FieldLike): string | null {
  const s = field.config?.subheading
  return typeof s === 'string' && s.trim() ? s.trim() : null
}

// --- values ----------------------------------------------------------------------------------------------------

/** No value: null, undefined, an empty text or an empty list. */
export function isEmptyValue(value: unknown): boolean {
  return (
    value === null ||
    value === undefined ||
    (typeof value === 'string' && value.trim() === '') ||
    (Array.isArray(value) && value.length === 0)
  )
}

const emailPattern = /^[^@\s]+@[^@\s]+\.[^@\s]+$/
const dateTimePattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?Z$/
const datePattern = /^\d{4}-\d{2}-\d{2}$/
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

function validDate(text: string): boolean {
  if (!datePattern.test(text)) return false
  const [y, m, d] = text.split('-').map(Number)
  const date = new Date(Date.UTC(y!, m! - 1, d!))
  return date.getUTCFullYear() === y && date.getUTCMonth() === m! - 1 && date.getUTCDate() === d
}

function validUrl(text: string): boolean {
  try {
    const url = new URL(text)
    return ['http:', 'https:', 'mailto:'].includes(url.protocol)
  } catch {
    return false
  }
}

/**
 * Why a value doesn't fit the field (the server's `FieldValueRules.Check` and the `required` rule), or null. JSON null
 * always fits a field that isn't required.
 */
export function valueError(field: FieldLike, value: unknown): string | null {
  if (isEmptyValue(value)) return field.required ? 'The field is required.' : null
  const c = field.config ?? {}
  switch (field.type) {
    case 'score':
      return 'Score values are computed, never stored.'
    case 'text':
    case 'textarea':
    case 'markdown':
    case 'url':
    case 'email': {
      if (typeof value !== 'string') return 'A text.'
      const max = field.type === 'text' ? (Number.isInteger(c.maxLength) ? (c.maxLength as number) : 10_000) : 1_000_000
      if (value.length > max) return `At most ${max.toLocaleString('en')} characters.`
      if (field.type === 'url' && !validUrl(value)) return 'An http(s) or mailto address.'
      if (field.type === 'email' && !emailPattern.test(value)) return 'An e-mail address.'
      return null
    }
    case 'number': {
      if (typeof value !== 'number' || !Number.isFinite(value)) return 'A number.'
      const { min, max } = numberSettings(c)
      if ((min !== null && value < min) || (max !== null && value > max)) return 'Outside the field’s min/max.'
      return null
    }
    case 'date':
      return typeof value === 'string' && validDate(value) ? null : 'A date yyyy-MM-dd.'
    case 'datetime':
      return typeof value === 'string' && dateTimePattern.test(value) && !Number.isNaN(Date.parse(value))
        ? null
        : 'A UTC timestamp yyyy-MM-ddTHH:mm:ssZ.'
    case 'bool':
      return typeof value === 'boolean' ? null : 'true or false.'
    case 'select':
      return optionOf(c, value) ? null : 'One of the field’s options (its id).'
    case 'multiselect': {
      if (!Array.isArray(value) || !value.every((v) => typeof v === 'string'))
        return 'A list of the field’s option ids.'
      if (new Set(value).size !== value.length) return 'Each option once.'
      return value.every((v) => optionOf(c, v)) ? null : 'Unknown option.'
    }
    case 'person': {
      if (!Array.isArray(value) || !value.every((v) => typeof v === 'string' && uuidPattern.test(v)))
        return 'A list of person ids.'
      if (new Set(value.map((v: string) => v.toLowerCase())).size !== value.length) return 'Each person once.'
      if (value.length > 1 && c.multiple !== true) return 'One person (the field takes one).'
      if (value.length > 100) return 'At most 100 persons.'
      return null
    }
    case 'rating': {
      const { min, max, allowNa } = ratingScale(c)
      if (value === 'na') return allowNa ? null : 'n/a is not allowed for this field.'
      return typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max
        ? null
        : `An integer from ${min} to ${max}${allowNa ? ' or "na"' : ''}.`
    }
    default:
      return 'Unknown field type.'
  }
}

type Draft = string | number | boolean | null

/**
 * The value an inline editor's draft stores: texts trimmed at the ends (empty → null), numbers rounded to the field's
 * decimals, dates as typed, a switch as true/false, a choice's option id.
 */
export function fromDraft(field: FieldLike, draft: Draft): unknown {
  switch (field.type) {
    case 'text':
    case 'url':
    case 'email':
    case 'textarea': {
      const text =
        typeof draft === 'string' ? (field.type === 'textarea' ? draft.replace(/\s+$/, '') : draft.trim()) : ''
      return text.trim() ? text : null
    }
    case 'number': {
      if (draft === null || draft === '') return null
      const n = typeof draft === 'number' ? draft : Number(draft)
      const { decimals } = numberSettings(field.config)
      return Number.isFinite(n) && decimals !== null ? Number(n.toFixed(decimals)) : n
    }
    case 'date':
      return typeof draft === 'string' && draft ? draft : null
    case 'bool':
      return draft === true
    case 'select':
      return typeof draft === 'string' && draft ? draft : null
    default:
      return draft
  }
}

/** The inline editor's draft for a stored value (the reverse of {@link fromDraft}). */
export function toDraft(field: FieldLike, value: unknown): Draft {
  if (value === null || value === undefined) return field.type === 'bool' ? false : null
  switch (field.type) {
    case 'bool':
      return value === true
    case 'number':
      return typeof value === 'number' ? value : null
    default:
      return typeof value === 'string' ? value : null
  }
}

/** A date-time value from the browser's `datetime-local` input (local time) as the stored UTC text, and back. */
export function dateTimeFromLocal(local: string): string | null {
  if (!local) return null
  const time = Date.parse(local)
  return Number.isNaN(time) ? null : new Date(time).toISOString().replace(/\.\d{3}Z$/, 'Z')
}

export function dateTimeToLocal(value: unknown): string {
  if (typeof value !== 'string') return ''
  const time = Date.parse(value)
  if (Number.isNaN(time)) return ''
  const d = new Date(time)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}
