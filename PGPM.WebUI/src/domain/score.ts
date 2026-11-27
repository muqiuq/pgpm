/**
 * Score fields (docs/03 §16, data model §7.4): computed from other fields of the same item, on the device (also offline),
 * never stored. The same specification is implemented in C# (PGPM/Config/ScoreEvaluator.cs); both run the test vectors
 * in spec/score-vectors.json.
 *
 * A result is `null` when the score is incomplete (an input has no value: the matrix tray "Not yet assessed") or can't
 * be evaluated (a value that doesn't fit its field, a select option without points, a configuration the validation
 * would refuse, a cycle).
 */

export type FieldType =
  | 'text'
  | 'textarea'
  | 'markdown'
  | 'number'
  | 'date'
  | 'datetime'
  | 'bool'
  | 'select'
  | 'multiselect'
  | 'person'
  | 'url'
  | 'email'
  | 'rating'
  | 'score'

export type ScoreOp = 'sum' | 'avg' | 'product' | 'count' | 'min' | 'max'
export type NaMode = 'rescale' | 'ignore' | 'zero'

export interface SelectOption {
  id: string
  label?: string
  color?: string
  points?: number | null
  archived?: boolean
}

/** `field_definitions.config` (data model §7.4): the members of every type, all optional; each type reads its own. */
export interface FieldConfig {
  /** number, rating */
  min?: number | null
  max?: number | null
  /** select, multiselect */
  options?: SelectOption[]
  multiPoints?: 'count' | 'sum'
  /** score */
  op?: ScoreOp
  inputs?: string[]
  na?: NaMode
  cap?: Record<string, number>
  scale?: number
  normalize?: boolean
  decimals?: number
  [other: string]: unknown
}

/** A field definition as far as scores need it. */
export interface FieldDef {
  key: string
  type: FieldType
  config?: FieldConfig | null
  isArchived?: boolean
}

/** A rating without `config.max` has the default scale 0–5. */
export const DEFAULT_RATING_MAX = 5
export const MAX_DECIMALS = 10
export const SCORE_OPS: readonly ScoreOp[] = ['sum', 'avg', 'product', 'count', 'min', 'max']
export const NA_MODES: readonly NaMode[] = ['rescale', 'ignore', 'zero']
/** The field types a score can take as input. */
export const NUMERIC_TYPES: readonly FieldType[] = ['number', 'rating', 'select', 'multiselect', 'score']

type Lookup<T> = Readonly<Record<string, T>>

/** An input's value (or n/a) and its maximum (null: none known). */
interface Input {
  value: number | 'na'
  max: number | null
}

interface ScoreSettings {
  op: ScoreOp
  inputs: string[]
  na: NaMode
  max: number | null
  cap: Lookup<number>
  scale: number
  normalize: boolean
  decimals: number
}

/** A record's own member only: field keys like `constructor` are valid (`^[a-z][a-z0-9_]{1,62}$`). */
function own<T>(record: Lookup<T>, key: string): T | undefined {
  return Object.hasOwn(record, key) ? record[key] : undefined
}

function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function configOf(def: FieldDef): FieldConfig {
  return def.config ?? {}
}

/** The score's configuration with its defaults, or null when it can't be evaluated. */
function settingsOf(def: FieldDef): ScoreSettings | null {
  if (def.type !== 'score') return null
  const c = configOf(def)
  const op = c.op
  const na = c.na ?? 'ignore'
  const decimals = c.decimals ?? 0
  const scale = c.scale ?? 1
  const cap = c.cap ?? {}
  if (!op || !SCORE_OPS.includes(op) || !NA_MODES.includes(na)) return null
  if (!Array.isArray(c.inputs) || !c.inputs.every((k) => typeof k === 'string')) return null
  if (!Number.isInteger(decimals) || decimals < 0 || decimals > MAX_DECIMALS || !isNumber(scale)) return null
  if (typeof cap !== 'object' || !Object.values(cap).every(isNumber)) return null
  return {
    op,
    inputs: c.inputs,
    na,
    max: isNumber(c.max) ? c.max : null,
    cap,
    scale,
    normalize: c.normalize === true,
    decimals,
  }
}

/** The score's maximum for display ("58 / 65") and as an input of other scores: 100 when normalised, else `max`. */
export function scoreMax(def: FieldDef): number | null {
  if (def.type !== 'score') return null
  const c = configOf(def)
  if (c.normalize === true) return 100
  return isNumber(c.max) ? c.max : null
}

/** A field's maximum as a score input (docs/03 §16), before the cap. Null: none known. */
export function inputMax(field: FieldDef): number | null {
  const c = configOf(field)
  const options = Array.isArray(c.options) ? c.options : []
  const points = options.map((o) => o.points).filter(isNumber)
  switch (field.type) {
    case 'number':
      return isNumber(c.max) ? c.max : null
    case 'rating':
      return isNumber(c.max) ? c.max : DEFAULT_RATING_MAX
    case 'select':
      return points.length ? Math.max(...points) : null
    case 'multiselect':
      return c.multiPoints === 'sum' ? points.reduce((a, b) => a + b, 0) : options.length
    case 'score':
      return scoreMax(field)
    default:
      return null
  }
}

/** The input's value, or null when it is missing or can't be used. */
function inputOf(
  field: FieldDef,
  fields: Lookup<FieldDef>,
  values: Lookup<unknown>,
  visiting: Set<string>,
): Input | null {
  const max = inputMax(field)
  if (field.type === 'score') {
    const value = evaluate(field, fields, values, visiting)
    return value === null ? null : { value, max }
  }
  const value = own(values, field.key)
  if (value === undefined || value === null) return null
  const options = configOf(field).options ?? []
  const pointsOf = (id: unknown) => {
    const points = typeof id === 'string' ? options.find((o) => o.id === id)?.points : undefined
    return isNumber(points) ? points : null
  }
  switch (field.type) {
    case 'number':
      return isNumber(value) ? { value, max } : null
    case 'rating':
      return value === 'na' || isNumber(value) ? { value, max } : null
    case 'select': {
      const points = pointsOf(value)
      return points === null ? null : { value: points, max }
    }
    case 'multiselect': {
      if (!Array.isArray(value) || !value.every((id) => typeof id === 'string')) return null
      const chosen = [...new Set(value)]
      if (!chosen.every((id) => options.some((o) => o.id === id))) return null
      if (configOf(field).multiPoints !== 'sum') return { value: chosen.length, max }
      const points = chosen.map(pointsOf)
      if (points.some((p) => p === null)) return null
      return { value: (points as number[]).reduce((a, b) => a + b, 0), max }
    }
    default:
      return null
  }
}

function evaluate(
  def: FieldDef,
  fields: Lookup<FieldDef>,
  values: Lookup<unknown>,
  visiting: Set<string>,
): number | null {
  const s = settingsOf(def)
  if (!s || visiting.has(def.key)) return null
  visiting.add(def.key)
  try {
    const inputs: Input[] = []
    for (const key of s.inputs) {
      const field = own(fields, key)
      const input = field ? inputOf(field, fields, values, visiting) : null
      // 1. Any input missing (or unusable): incomplete.
      if (!input) return null
      // 2. The per-input cap, on the value and on the maximum.
      const cap = own(s.cap, key)
      if (cap !== undefined) {
        inputs.push({
          value: input.value === 'na' ? 'na' : Math.min(input.value, cap),
          max: input.max === null ? cap : Math.min(input.max, cap),
        })
      } else {
        inputs.push(input)
      }
    }
    return combine(s, inputs)
  } finally {
    visiting.delete(def.key)
  }
}

/** Steps 3–6 of docs/03 §16 over the inputs' values. */
function combine(s: ScoreSettings, inputs: Input[]): number | null {
  // 3. n/a: zero counts it as 0, ignore and rescale drop it.
  const remaining =
    s.na === 'zero'
      ? inputs.map((i) => ({ value: i.value === 'na' ? 0 : i.value, max: i.max }))
      : inputs.flatMap((i) => (i.value === 'na' ? [] : [{ value: i.value, max: i.max }]))
  // 4. The operation; nothing remaining: no result.
  if (!remaining.length) return null
  const values = remaining.map((i) => i.value)
  let result: number
  switch (s.op) {
    case 'sum':
      result = values.reduce((a, b) => a + b, 0)
      break
    case 'avg':
      result = values.reduce((a, b) => a + b, 0) / values.length
      break
    case 'product':
      result = values.reduce((a, b) => a * b, 1)
      break
    case 'count':
      result = values.filter((v) => v !== 0).length
      break
    case 'min':
      result = Math.min(...values)
      break
    case 'max':
      result = Math.max(...values)
      break
  }
  // 3. (cont.) rescale, for sums: to max over the maxima of the answered inputs.
  if (s.na === 'rescale' && s.op === 'sum') {
    if (s.max === null || remaining.some((i) => i.max === null)) return null
    const answered = remaining.reduce((a, i) => a + (i.max as number), 0)
    if (answered <= 0) return null
    result = (result * s.max) / answered
  }
  // 5. Scale, then normalise to 0–100.
  result *= s.scale
  if (s.normalize) {
    if (s.max === null || s.max <= 0) return null
    result = (result * 100) / s.max
  }
  // 6. Rounded once, here.
  return Number.isFinite(result) ? roundHalfAwayFromZero(result, s.decimals) : null
}

/**
 * The score's value for an item: `fieldsByKey` holds every field definition (inputs and nested scores), `valuesByKey`
 * the item's field values (`field_values.value`) by field key.
 */
export function evaluateScore(
  def: FieldDef,
  fieldsByKey: Lookup<FieldDef>,
  valuesByKey: Lookup<unknown>,
): number | null {
  return evaluate(def, fieldsByKey, valuesByKey, new Set())
}

/** Shifts a non-negative number by powers of ten through its decimal text, so that no binary rounding creeps in. */
function shift(value: number, by: number): number {
  const [mantissa, exponent = '0'] = String(value).split('e')
  return Number(`${mantissa}e${Number(exponent) + by}`)
}

/**
 * Round half away from zero (docs/03 §16 step 6, as C#'s `MidpointRounding.AwayFromZero` on `decimal`): 2.5 → 3,
 * −2.5 → −3, 0.125 → 0.13. The value is first cut to the 15 significant digits a double holds, so binary noise from the
 * steps before (1.001 + 0.004 = 1.00499…) doesn't decide the midpoint. Never −0.
 */
export function roundHalfAwayFromZero(value: number, decimals: number): number {
  if (!Number.isFinite(value)) return value
  const clean = Number(value.toPrecision(15))
  const rounded = shift(Math.round(shift(Math.abs(clean), decimals)), -decimals)
  return clean < 0 && rounded !== 0 ? -rounded : rounded
}

export type ScoreErrorCode =
  | 'not_a_score'
  | 'invalid_op'
  | 'invalid_na'
  | 'invalid_decimals'
  | 'no_inputs'
  | 'duplicate_input'
  | 'unknown_input'
  | 'archived_input'
  | 'not_numeric'
  | 'options_without_points'
  | 'invalid_cap'
  | 'cycle'
  | 'max_required'
  | 'input_max_required'

export interface ScoreConfigError {
  code: ScoreErrorCode
  /** The input (or cap key) it is about. */
  input?: string
}

/**
 * Validation on save (docs/03 §16, online, also on the server): inputs exist, are not archived, have numeric meaning,
 * no cycles; rescale (of a sum) and normalize need maxima. `allDefs` are the stored definitions; `def` replaces the one
 * with its key. No errors: an empty list.
 */
export function validateScoreConfig(def: FieldDef, allDefs: readonly FieldDef[]): ScoreConfigError[] {
  if (def.type !== 'score') return [{ code: 'not_a_score' }]
  const byKey = new Map(allDefs.map((d) => [d.key, d]))
  byKey.set(def.key, def)
  const c = configOf(def)
  const errors: ScoreConfigError[] = []
  if (!c.op || !SCORE_OPS.includes(c.op)) errors.push({ code: 'invalid_op' })
  if (c.na !== undefined && !NA_MODES.includes(c.na)) errors.push({ code: 'invalid_na' })
  if (c.decimals !== undefined && !(Number.isInteger(c.decimals) && c.decimals >= 0 && c.decimals <= MAX_DECIMALS)) {
    errors.push({ code: 'invalid_decimals' })
  }
  const inputs = Array.isArray(c.inputs) ? c.inputs : []
  if (!inputs.length) errors.push({ code: 'no_inputs' })
  const seen = new Set<string>()
  const reported = new Set<string>()
  for (const key of inputs) {
    if (seen.has(key) && !reported.has(key)) {
      errors.push({ code: 'duplicate_input', input: key })
      reported.add(key)
    }
    seen.add(key)
  }
  const cap = c.cap ?? {}
  for (const [key, value] of Object.entries(cap)) {
    if (!seen.has(key) || !isNumber(value)) errors.push({ code: 'invalid_cap', input: key })
  }

  const usable: FieldDef[] = []
  for (const key of seen) {
    const field = byKey.get(key)
    if (!field) {
      errors.push({ code: 'unknown_input', input: key })
    } else if (field.isArchived) {
      errors.push({ code: 'archived_input', input: key })
    } else if (!NUMERIC_TYPES.includes(field.type)) {
      errors.push({ code: 'not_numeric', input: key })
    } else if (needsPoints(field)) {
      errors.push({ code: 'options_without_points', input: key })
    } else if (field.type === 'score' && reachesCycle(field, def.key, byKey, new Set())) {
      errors.push({ code: 'cycle', input: key })
    } else {
      usable.push(field)
    }
  }

  const hasMax = isNumber(c.max) && c.max > 0
  const rescales = (c.na ?? 'ignore') === 'rescale' && c.op === 'sum'
  if ((rescales || c.normalize === true) && !hasMax) errors.push({ code: 'max_required' })
  if (rescales) {
    for (const field of usable) {
      const capped = own(cap, field.key)
      if (inputMax(field) === null && !isNumber(capped)) errors.push({ code: 'input_max_required', input: field.key })
    }
  }
  return errors
}

/** A select (or a multiselect summing points) whose options in use don't all carry points. */
function needsPoints(field: FieldDef): boolean {
  const c = configOf(field)
  if (field.type !== 'select' && !(field.type === 'multiselect' && c.multiPoints === 'sum')) return false
  return (c.options ?? []).some((o) => !o.archived && !isNumber(o.points))
}

/** True when evaluating `field` leads back to `root` or into any other cycle. */
function reachesCycle(field: FieldDef, root: string, byKey: Map<string, FieldDef>, path: Set<string>): boolean {
  if (field.key === root || path.has(field.key)) return true
  path.add(field.key)
  try {
    for (const key of configOf(field).inputs ?? []) {
      const input = byKey.get(key)
      if (input?.type === 'score' && reachesCycle(input, root, byKey, path)) return true
    }
    return false
  } finally {
    path.delete(field.key)
  }
}
