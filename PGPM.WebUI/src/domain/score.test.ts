// @vitest-environment node
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import {
  evaluateScore,
  inputMax,
  roundHalfAwayFromZero,
  scoreMax,
  validateScoreConfig,
  type FieldConfig,
  type FieldDef,
  type ScoreConfigError,
} from './score'

/** spec/score-vectors.json (docs/03 §16), shared with the C# tests; run-tests.sh and tools/npm.sh provide ../spec. */
interface Vectors {
  fieldSets: Record<string, FieldDef[]>
  cases: {
    name: string
    use: string[]
    fields?: FieldDef[]
    values: Record<string, unknown>
    expect: Record<string, number | null>
    expectMax?: Record<string, number | null>
  }[]
  validation: {
    name: string
    use: string[]
    fields?: FieldDef[]
    score: string
    replace?: FieldDef
    expectErrors: ScoreConfigError[]
  }[]
}

const vectors = JSON.parse(
  readFileSync(fileURLToPath(new URL('../../../spec/score-vectors.json', import.meta.url).href), 'utf8'),
) as Vectors

/** A case's fields: its sets in order, then its own; a later definition replaces an earlier one with the same key. */
function fieldsOf(c: { use: string[]; fields?: FieldDef[] }): Record<string, FieldDef> {
  const all = [...c.use.flatMap((name) => vectors.fieldSets[name] ?? []), ...(c.fields ?? [])]
  return Object.fromEntries(all.map((f) => [f.key, f]))
}

const byCode = (a: ScoreConfigError, b: ScoreConfigError) =>
  `${a.code}:${a.input ?? ''}`.localeCompare(`${b.code}:${b.input ?? ''}`)

describe('score evaluation (spec/score-vectors.json)', () => {
  it('has vectors for every op and n/a mode', () => {
    const configs = vectors.cases.flatMap((c) => Object.values(fieldsOf(c))).map((f) => f.config ?? {})
    for (const op of ['sum', 'avg', 'product', 'count', 'min', 'max'])
      expect(configs.some((c) => c.op === op)).toBe(true)
    for (const na of ['rescale', 'ignore', 'zero']) expect(configs.some((c) => c.na === na)).toBe(true)
  })

  for (const c of vectors.cases) {
    it(c.name, () => {
      const fields = fieldsOf(c)
      for (const [key, expected] of Object.entries(c.expect)) {
        expect({ key, value: evaluateScore(fields[key]!, fields, c.values) }).toEqual({ key, value: expected })
      }
      for (const [key, expected] of Object.entries(c.expectMax ?? {})) {
        expect({ key, max: scoreMax(fields[key]!) }).toEqual({ key, max: expected })
      }
    })
  }
})

describe('score validation (spec/score-vectors.json)', () => {
  for (const c of vectors.validation) {
    it(c.name, () => {
      const fields = fieldsOf(c)
      const def = c.replace ?? fields[c.score]!
      const errors = validateScoreConfig(def, Object.values(fields))
      expect([...errors].sort(byCode)).toEqual([...c.expectErrors].sort(byCode))
    })
  }
})

describe('score evaluation beyond the vectors', () => {
  const rating: FieldDef = { key: 'r', type: 'rating', config: { max: 5, allowNa: true } }

  it('rounds half away from zero, never to −0', () => {
    expect(roundHalfAwayFromZero(2.5, 0)).toBe(3)
    expect(roundHalfAwayFromZero(-2.5, 0)).toBe(-3)
    expect(roundHalfAwayFromZero(0.125, 2)).toBe(0.13)
    expect(roundHalfAwayFromZero(1.005, 2)).toBe(1.01)
    expect(Object.is(roundHalfAwayFromZero(-0.4, 0), 0)).toBe(true)
    expect(Object.is(roundHalfAwayFromZero(-0, 2), 0)).toBe(true)
    expect(roundHalfAwayFromZero(1e-7, 2)).toBe(0)
    expect(roundHalfAwayFromZero(1.5e21, 0)).toBe(1.5e21)
    expect(roundHalfAwayFromZero(Number.NaN, 0)).toBeNaN()
  })

  it('takes field keys literally, also those of Object.prototype', () => {
    const constructor: FieldDef = { key: 'constructor', type: 'number', config: {} }
    const score: FieldDef = { key: 'x', type: 'score', config: { op: 'sum', inputs: ['constructor'] } }
    expect(evaluateScore(score, { x: score }, {})).toBeNull()
    expect(evaluateScore(score, { x: score, constructor }, {})).toBeNull()
    expect(evaluateScore(score, { x: score, constructor }, { constructor: 4 })).toBe(4)
  })

  it('refuses configurations the validation refuses', () => {
    const values = { r: 3 }
    const score = (config: Record<string, unknown>): FieldDef => ({
      key: 'x',
      type: 'score',
      config: config as FieldConfig,
    })
    expect(evaluateScore(score({ op: 'median', inputs: ['r'] }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'], na: 'skip' }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'], decimals: 1.5 }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'], cap: { r: '2' } }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: 'r' }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'], normalize: true }), { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'], na: 'rescale' }), { r: rating }, values)).toBeNull()
    expect(evaluateScore({ ...rating, type: 'number' }, { r: rating }, values)).toBeNull()
    expect(evaluateScore(score({ op: 'sum', inputs: ['r'] }), { r: rating }, values)).toBe(3)
  })

  it('gives null for results beyond a double', () => {
    const big: FieldDef = { key: 'n', type: 'number', config: {} }
    const score: FieldDef = { key: 'x', type: 'score', config: { op: 'product', inputs: ['n', 'n'] } }
    expect(evaluateScore(score, { n: big, x: score }, { n: 1e200 })).toBeNull()
  })

  it('knows the maxima of the input types', () => {
    expect(inputMax({ key: 'n', type: 'number', config: { max: 12 } })).toBe(12)
    expect(inputMax({ key: 'n', type: 'number' })).toBeNull()
    expect(inputMax({ key: 'r', type: 'rating' })).toBe(5)
    expect(inputMax({ key: 's', type: 'select', config: { options: [{ id: 'a' }] } })).toBeNull()
    expect(inputMax({ key: 'm', type: 'multiselect', config: { options: [{ id: 'a' }, { id: 'b' }] } })).toBe(2)
    expect(inputMax({ key: 't', type: 'text' })).toBeNull()
    expect(scoreMax({ key: 't', type: 'text' })).toBeNull()
  })

  it('validates a score whose op is missing and whose cap is not a number', () => {
    const def: FieldDef = { key: 'x', type: 'score', config: { inputs: ['r'], cap: { r: Number.NaN } } }
    expect(validateScoreConfig(def, [rating]).sort(byCode)).toEqual([
      { code: 'invalid_cap', input: 'r' },
      { code: 'invalid_op' },
    ])
  })
})
