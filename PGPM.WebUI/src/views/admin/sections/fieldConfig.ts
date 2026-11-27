import {
  inputMax,
  NUMERIC_TYPES,
  scoreMax,
  type FieldConfig,
  type FieldDef,
  type FieldType,
  type NaMode,
  type ScoreConfigError,
  type ScoreOp,
  type SelectOption,
} from '@/domain/score'

/**
 * The rules of the field dialog (docs/01 §6.1, data model §7.4, docs/03 §16), pure so that they are tested without a
 * page: field keys and option ids, the type-specific configuration as a form and back, the score editor's form and its
 * validation messages.
 */

/** `field_definitions.key` (data model §7.4): stable, immutable after creation. */
export const FIELD_KEY_PATTERN = /^[a-z][a-z0-9_]{1,62}$/
/** A select option's id (the server's `FieldValueRules`): stable, labels may change. */
export const OPTION_ID_PATTERN = /^[a-z0-9][a-z0-9_]{0,62}$/

export const fieldTypes: readonly { value: FieldType; label: string }[] = [
  { value: 'text', label: 'Text (single line)' },
  { value: 'textarea', label: 'Text (multi-line)' },
  { value: 'markdown', label: 'Markdown' },
  { value: 'number', label: 'Number' },
  { value: 'date', label: 'Date' },
  { value: 'datetime', label: 'Date-time' },
  { value: 'bool', label: 'Checkbox' },
  { value: 'select', label: 'Single select' },
  { value: 'multiselect', label: 'Multi select' },
  { value: 'person', label: 'Person' },
  { value: 'url', label: 'URL' },
  { value: 'email', label: 'E-mail' },
  { value: 'rating', label: 'Rating' },
  { value: 'score', label: 'Score (computed)' },
]

export function typeLabel(type: FieldType): string {
  return fieldTypes.find((t) => t.value === type)?.label ?? type
}

/** Lower case, words joined by `_`, without diacritics. */
function slug(text: string): string {
  return text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
}

/** A field key suggested by a label: "Time to proof of value" → `time_to_proof_of_value`. */
export function keyFromLabel(label: string): string {
  let key = slug(label).replace(/^[0-9_]+/, '')
  if (key.length < 2) key = key ? `${key}_field` : ''
  return key.slice(0, 63).replace(/_+$/, '')
}

/** An option id from its label, unique among `taken`: "< 3 months" → `3_months`, then `3_months_2`. */
export function optionIdFromLabel(label: string, taken: Iterable<string>): string {
  const used = new Set(taken)
  const base = slug(label).slice(0, 56).replace(/_+$/, '') || 'option'
  if (!used.has(base)) return base
  for (let n = 2; ; n++) {
    const id = `${base}_${n}`
    if (!used.has(id)) return id
  }
}

// --- select options -----------------------------------------------------------------------------------------------

/** An option row of the editor: `saved` options keep their id (values refer to it); new ones may still change it. */
export interface OptionForm {
  id: string
  label: string
  color: string | null
  points: number | null
  archived: boolean
  saved: boolean
}

export function optionForms(config: FieldConfig | null | undefined): OptionForm[] {
  return (config?.options ?? []).map((o) => ({
    id: o.id,
    label: o.label ?? o.id,
    color: o.color ?? null,
    points: typeof o.points === 'number' ? o.points : null,
    archived: o.archived === true,
    saved: true,
  }))
}

export function optionsOf(forms: readonly OptionForm[]): SelectOption[] {
  return forms.map((o) => ({
    id: o.id,
    label: o.label.trim(),
    ...(o.color ? { color: o.color } : {}),
    ...(o.points !== null ? { points: o.points } : {}),
    ...(o.archived ? { archived: true } : {}),
  }))
}

/** The problems of the options: per row index, or `options` for the list. */
export function optionErrors(forms: readonly OptionForm[]): Record<string, string> {
  const errors: Record<string, string> = {}
  if (!forms.length) errors.options = 'At least one option.'
  const seen = new Set<string>()
  forms.forEach((o, i) => {
    if (!o.label.trim()) errors[`${i}.label`] = 'A label is needed.'
    else if (o.label.trim().length > 200) errors[`${i}.label`] = 'At most 200 characters.'
    if (!OPTION_ID_PATTERN.test(o.id))
      errors[`${i}.id`] = 'Lower case letters, digits and _ (starting with one of them).'
    else if (seen.has(o.id)) errors[`${i}.id`] = 'This id is taken.'
    seen.add(o.id)
    if (o.color && !/^#[0-9a-f]{6}$/i.test(o.color)) errors[`${i}.color`] = 'A colour #rrggbb.'
  })
  return errors
}

// --- the type-specific configuration ------------------------------------------------------------------------------

/** Every member of every type's configuration; each type reads its own. */
export interface ConfigForm {
  subheading: string
  maxLength: number | null
  placeholder: string
  decimals: number | null
  min: number | null
  max: number | null
  unit: string
  timebox: boolean
  options: OptionForm[]
  multiPoints: 'count' | 'sum'
  multiple: boolean
  ratingMin: number
  ratingMax: number
  allowNa: boolean
  /** Rating labels by value ('' = none). */
  labels: Record<string, string>
  score: ScoreForm
}

const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown) => (typeof v === 'string' ? v : '')

export function configForm(type: FieldType, config: FieldConfig | null | undefined): ConfigForm {
  const c = config ?? {}
  return {
    subheading: str(c.subheading),
    maxLength: num(c.maxLength),
    placeholder: str(c.placeholder),
    decimals: num(c.decimals),
    min: type === 'rating' ? null : num(c.min),
    max: type === 'rating' ? null : num(c.max),
    unit: str(c.unit),
    timebox: c.timebox === true,
    options: optionForms(c),
    multiPoints: c.multiPoints === 'sum' ? 'sum' : 'count',
    multiple: c.multiple === true,
    ratingMin: type === 'rating' ? (num(c.min) ?? 0) : 0,
    ratingMax: type === 'rating' ? (num(c.max) ?? 5) : 5,
    allowNa: c.allowNa === true,
    labels: Object.fromEntries(
      Object.entries(typeof c.labels === 'object' && c.labels !== null ? (c.labels as Record<string, unknown>) : {})
        .filter(([, v]) => typeof v === 'string')
        .map(([k, v]) => [k, v as string]),
    ),
    score: scoreForm(type === 'score' ? c : null),
  }
}

/** The stored configuration of a type from the form: only the type's members, nothing empty. */
export function configOf(type: FieldType, form: ConfigForm): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  if (form.subheading.trim()) out.subheading = form.subheading.trim()
  switch (type) {
    case 'text':
      if (form.maxLength !== null) out.maxLength = form.maxLength
      if (form.placeholder.trim()) out.placeholder = form.placeholder.trim()
      break
    case 'number':
      if (form.decimals !== null) out.decimals = form.decimals
      out.min = form.min
      out.max = form.max
      if (form.unit.trim()) out.unit = form.unit.trim()
      break
    case 'date':
    case 'datetime':
      if (form.timebox) out.timebox = true
      break
    case 'select':
      out.options = optionsOf(form.options)
      break
    case 'multiselect':
      out.options = optionsOf(form.options)
      out.multiPoints = form.multiPoints
      break
    case 'person':
      out.multiple = form.multiple
      break
    case 'rating': {
      out.min = form.ratingMin
      out.max = form.ratingMax
      out.allowNa = form.allowNa
      const labels = Object.fromEntries(
        Object.entries(form.labels)
          .filter(([k, v]) => v.trim() && Number(k) >= form.ratingMin && Number(k) <= form.ratingMax)
          .map(([k, v]) => [k, v.trim()]),
      )
      if (Object.keys(labels).length) out.labels = labels
      break
    }
    case 'score':
      Object.assign(out, scoreConfig(form.score))
      break
  }
  return out
}

/** The configuration's problems the dialog can tell before the server: by form member. */
export function configErrors(type: FieldType, form: ConfigForm): Record<string, string> {
  const errors: Record<string, string> = {}
  if (form.subheading.trim().length > 100) errors.subheading = 'At most 100 characters.'
  switch (type) {
    case 'text':
      if (
        form.maxLength !== null &&
        !(Number.isInteger(form.maxLength) && form.maxLength >= 1 && form.maxLength <= 10_000)
      )
        errors.maxLength = '1 to 10,000.'
      break
    case 'number':
      if (form.min !== null && form.max !== null && form.min > form.max) errors.max = 'Not below the minimum.'
      if (form.decimals !== null && !(Number.isInteger(form.decimals) && form.decimals >= 0 && form.decimals <= 6))
        errors.decimals = '0 to 6.'
      break
    case 'select':
    case 'multiselect':
      for (const [k, v] of Object.entries(optionErrors(form.options))) errors[`options.${k}`] = v
      break
    case 'rating':
      if (!Number.isInteger(form.ratingMin) || !Number.isInteger(form.ratingMax) || form.ratingMin >= form.ratingMax)
        errors.ratingMax = 'Whole numbers, the maximum above the minimum.'
      else if (form.ratingMax - form.ratingMin > 20) errors.ratingMax = 'At most 20 steps.'
      break
  }
  return errors
}

// --- the score editor -------------------------------------------------------------------------------------------

export interface ScoreForm {
  op: ScoreOp
  inputs: string[]
  na: NaMode
  max: number | null
  /** Per input key; null: no cap. */
  caps: Record<string, number | null>
  scale: number | null
  normalize: boolean
  decimals: number
}

export function scoreForm(config: FieldConfig | null | undefined): ScoreForm {
  const c = config ?? {}
  return {
    op: c.op ?? 'sum',
    inputs: Array.isArray(c.inputs) ? [...c.inputs] : [],
    na: c.na ?? 'ignore',
    max: num(c.max),
    caps: { ...(c.cap ?? {}) },
    scale: num(c.scale),
    normalize: c.normalize === true,
    decimals: Number.isInteger(c.decimals) ? (c.decimals as number) : 0,
  }
}

/** The stored score configuration (data model §7.4): caps of inputs only, `scale` 1 unless set. */
export function scoreConfig(form: ScoreForm): FieldConfig {
  const cap = Object.fromEntries(
    Object.entries(form.caps).filter(
      (entry): entry is [string, number] => form.inputs.includes(entry[0]) && typeof entry[1] === 'number',
    ),
  )
  return {
    op: form.op,
    inputs: [...form.inputs],
    na: form.na,
    ...(form.max !== null ? { max: form.max } : {}),
    ...(Object.keys(cap).length ? { cap } : {}),
    scale: form.scale ?? 1,
    normalize: form.normalize,
    decimals: form.decimals,
  }
}

/** The fields a score may take as input: numeric, not archived, not the score itself. */
export function scoreInputCandidates(defs: readonly (FieldDef & { label?: string })[], ownKey: string) {
  return defs.filter((d) => NUMERIC_TYPES.includes(d.type) && !d.isArchived && d.key !== ownKey)
}

export const opLabels: Record<ScoreOp, string> = {
  sum: 'Sum',
  avg: 'Average',
  product: 'Product',
  count: 'Count (non-zero inputs)',
  min: 'Minimum',
  max: 'Maximum',
}

export const naLabels: Record<NaMode, string> = {
  rescale: 'Rescale – leave n/a out, scale the sum to the maximum',
  ignore: 'Ignore – leave n/a out',
  zero: 'Count n/a as 0',
}

/** A validation error of docs/03 §16 as a sentence; `name` gives an input's label. */
export function scoreErrorMessage(error: ScoreConfigError, name: (key: string) => string = (k) => k): string {
  const input = error.input ? name(error.input) : ''
  switch (error.code) {
    case 'not_a_score':
      return 'Not a score.'
    case 'invalid_op':
      return 'Choose an operation.'
    case 'invalid_na':
      return 'Choose how n/a answers count.'
    case 'invalid_decimals':
      return 'Decimals: a whole number from 0 to 10.'
    case 'no_inputs':
      return 'Pick at least one input.'
    case 'duplicate_input':
      return `${input} is an input twice.`
    case 'unknown_input':
      return `${input} doesn’t exist (any more).`
    case 'archived_input':
      return `${input} is archived.`
    case 'not_numeric':
      return `${input} has no numeric meaning (numbers, ratings, selects with points, multi selects and scores have).`
    case 'options_without_points':
      return `${input} has options without points.`
    case 'invalid_cap':
      return `The cap of ${input} is not a number for one of the inputs.`
    case 'cycle':
      return `${input} leads back to this score (a cycle).`
    case 'max_required':
      return 'A maximum above 0 is needed for a rescaled sum or a normalised score.'
    case 'input_max_required':
      return `${input} has no maximum: give it one, or a cap (rescaling needs it).`
  }
}

const scoreCodes = new Set<string>([
  'not_a_score',
  'invalid_op',
  'invalid_na',
  'invalid_decimals',
  'no_inputs',
  'duplicate_input',
  'unknown_input',
  'archived_input',
  'not_numeric',
  'options_without_points',
  'invalid_cap',
  'cycle',
  'max_required',
  'input_max_required',
])

/**
 * A message of the server's `errors.config` as a sentence: the field's own score problems come as `code` or
 * `code: input` (ConfigService), others (a score that would break, a configuration problem) are kept as they are.
 */
export function serverConfigMessage(message: string, name: (key: string) => string = (k) => k): string {
  const match = /^([a-z_]+)(?:: (.+))?$/.exec(message.trim())
  if (!match || !scoreCodes.has(match[1]!)) return message
  return scoreErrorMessage({ code: match[1] as ScoreConfigError['code'], input: match[2] }, name)
}

/** An input's maximum after its cap (null: none known). */
export function cappedMax(def: FieldDef, cap: number | null | undefined): number | null {
  const max = inputMax(def)
  if (typeof cap !== 'number') return max
  return max === null ? cap : Math.min(max, cap)
}

/**
 * What the score will show: "Shown as … / 65" (normalised: "… / 100"), and for a sum the inputs' maxima added up –
 * the maximum a rescaled sum needs ("the inputs' maxima add up to 65").
 */
export function scorePreview(form: ScoreForm, defs: Readonly<Record<string, FieldDef>>): string {
  const max = scoreMax({ key: '_', type: 'score', config: scoreConfig(form) })
  const parts = [max === null ? 'Shown without a maximum' : `Shown as … / ${max}`]
  if (form.op === 'sum' && form.inputs.length) {
    const maxima = form.inputs.map((k) => (defs[k] ? cappedMax(defs[k]!, form.caps[k]) : null))
    if (maxima.every((m) => m !== null)) {
      const total = (maxima as number[]).reduce((a, b) => a + b, 0)
      parts.push(`the inputs’ maxima add up to ${total}`)
    } else {
      parts.push('not every input has a maximum')
    }
  }
  return parts.join(' · ')
}
