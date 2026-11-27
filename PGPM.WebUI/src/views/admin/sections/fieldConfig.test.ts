import { describe, expect, it } from 'vitest'
import { validateScoreConfig, type FieldDef } from '@/domain/score'
import {
  configErrors,
  configForm,
  configOf,
  keyFromLabel,
  optionErrors,
  optionIdFromLabel,
  optionsOf,
  scoreConfig,
  scoreErrorMessage,
  scoreForm,
  scorePreview,
  serverConfigMessage,
  type OptionForm,
} from './fieldConfig'

const rating = (key: string): FieldDef => ({ key, type: 'rating', config: { min: 0, max: 5, allowNa: true } })
const feasibilityInputs = [
  'feas_data_access',
  'feas_infra',
  'feas_data_quality',
  'feas_solution_known',
  'feas_similar_built',
  'feas_technologies',
  'feas_no_new_processes',
  'feas_processes_adaptable',
  'feas_no_major_changes',
  'feas_tech_knowhow',
  'feas_domain_knowhow',
  'feas_ai_understanding',
]
const timeToValue: FieldDef = {
  key: 'time_to_proof_of_value',
  type: 'select',
  config: {
    options: [
      { id: 'lt3', label: '< 3 months', points: 5 },
      { id: 'gt12', label: '> 12 months', points: 1 },
    ],
  },
}
const defs: FieldDef[] = [...feasibilityInputs.map(rating), timeToValue]
const byKey = Object.fromEntries(defs.map((d) => [d.key, d]))

describe('field keys and option ids (data model §7.4)', () => {
  it('suggests a key from the label, starting with a letter', () => {
    expect(keyFromLabel('Time to proof of value')).toBe('time_to_proof_of_value')
    expect(keyFromLabel('Geschäftswert (CHF)')).toBe('geschaftswert_chf')
    expect(keyFromLabel('3 Ratings')).toBe('ratings')
    expect(keyFromLabel('x')).toBe('x_field')
    expect(keyFromLabel('')).toBe('')
  })

  it('makes option ids from labels, unique among the taken ones', () => {
    expect(optionIdFromLabel('< 3 months', [])).toBe('3_months')
    expect(optionIdFromLabel('< 3 months', ['3_months'])).toBe('3_months_2')
    expect(optionIdFromLabel('< 3 months', ['3_months', '3_months_2'])).toBe('3_months_3')
    expect(optionIdFromLabel('???', [])).toBe('option')
  })

  it('checks the options: labels, id pattern, unique ids, colours, at least one', () => {
    const o = (id: string, label: string, color: string | null = null): OptionForm => ({
      id,
      label,
      color,
      points: null,
      archived: false,
      saved: false,
    })
    expect(optionErrors([])).toEqual({ options: 'At least one option.' })
    expect(optionErrors([o('go', 'Go'), o('go', 'Go again'), o('-x', ''), o('ok', 'Ok', 'red')])).toEqual({
      '1.id': 'This id is taken.',
      '2.label': 'A label is needed.',
      '2.id': 'Lower case letters, digits and _ (starting with one of them).',
      '3.color': 'A colour #rrggbb.',
    })
    expect(
      optionsOf([{ id: 'lt3', label: ' < 3 months ', color: '#16a34a', points: 5, archived: true, saved: true }]),
    ).toEqual([{ id: 'lt3', label: '< 3 months', color: '#16a34a', points: 5, archived: true }])
  })
})

describe('the type-specific configuration', () => {
  it('keeps only the type’s members and drops empty ones', () => {
    const form = configForm('number', { decimals: 0, min: 0, unit: 'CHF / year', options: [], subheading: '' })
    expect(configOf('number', form)).toEqual({ decimals: 0, min: 0, max: null, unit: 'CHF / year' })
    expect(configOf('text', { ...configForm('text', null), maxLength: 200, placeholder: ' ' })).toEqual({
      maxLength: 200,
    })
    expect(configOf('date', { ...configForm('date', null), timebox: true, subheading: ' Charter ' })).toEqual({
      subheading: 'Charter',
      timebox: true,
    })
    expect(configOf('person', configForm('person', { multiple: true }))).toEqual({ multiple: true })
  })

  it('round-trips a rating with labels inside its scale', () => {
    const config = {
      subheading: 'Data & infrastructure',
      min: 0,
      max: 5,
      allowNa: true,
      labels: { 0: 'not at all', 5: 'fully' },
    }
    const form = configForm('rating', config)
    expect(form.ratingMax).toBe(5)
    expect(configOf('rating', form)).toEqual({ ...config, labels: { '0': 'not at all', '5': 'fully' } })
    expect(configOf('rating', { ...form, ratingMax: 3 }).labels).toEqual({ '0': 'not at all' })
    expect(configErrors('rating', { ...form, ratingMin: 5 })).toEqual({
      ratingMax: 'Whole numbers, the maximum above the minimum.',
    })
  })

  it('round-trips a multiselect’s options and how it counts in scores', () => {
    const config = {
      options: [
        { id: 'a', label: 'A' },
        { id: 'b', label: 'B', points: 2, color: '#dc2626' },
      ],
      multiPoints: 'sum' as const,
    }
    expect(configOf('multiselect', configForm('multiselect', config))).toEqual(config)
    expect(configErrors('number', { ...configForm('number', { min: 5, max: 1 }) })).toEqual({
      max: 'Not below the minimum.',
    })
  })
})

describe('the score editor (docs/03 §16)', () => {
  it('turns the form into the stored configuration and back', () => {
    const config = {
      op: 'sum' as const,
      inputs: [...feasibilityInputs, 'time_to_proof_of_value'],
      na: 'rescale' as const,
      max: 65,
      scale: 1,
      normalize: false,
      decimals: 0,
    }
    expect(scoreConfig(scoreForm(config))).toEqual(config)
    const form = { ...scoreForm(config), caps: { feas_infra: 3, gone: 2, feas_tech_knowhow: null } }
    expect(scoreConfig(form).cap).toEqual({ feas_infra: 3 })
    expect(scoreForm(null)).toMatchObject({ op: 'sum', inputs: [], na: 'ignore', decimals: 0 })
  })

  it('previews what the score shows and what the inputs’ maxima add up to', () => {
    const form = scoreForm({
      op: 'sum',
      inputs: [...feasibilityInputs, 'time_to_proof_of_value'],
      na: 'rescale',
      max: 65,
    })
    expect(scorePreview(form, byKey)).toBe('Shown as … / 65 · the inputs’ maxima add up to 65')
    expect(scorePreview({ ...form, caps: { feas_infra: 3 } }, byKey)).toContain('add up to 63')
    expect(scorePreview({ ...form, normalize: true }, byKey)).toContain('… / 100')
    expect(scorePreview({ ...form, inputs: ['nope'], max: null }, byKey)).toBe(
      'Shown without a maximum · not every input has a maximum',
    )
  })

  it('validates with the shared rules and says why in words', () => {
    const good = {
      key: 'feasibility_index',
      type: 'score' as const,
      config: scoreConfig(scoreForm({ op: 'sum', inputs: feasibilityInputs, na: 'rescale', max: 60 })),
    }
    expect(validateScoreConfig(good, defs)).toEqual([])
    const bad = {
      ...good,
      config: scoreConfig({ ...scoreForm(good.config), inputs: ['feas_infra', 'nope'], max: null }),
    }
    const errors = validateScoreConfig(bad, defs)
    expect(errors.map((e) => e.code).sort()).toEqual(['max_required', 'unknown_input'])
    const names = (k: string) => (k === 'feas_infra' ? 'Infrastructure' : k)
    expect(errors.map((e) => scoreErrorMessage(e, names))).toContain('nope doesn’t exist (any more).')
    expect(scoreErrorMessage({ code: 'cycle', input: 'feas_infra' }, names)).toBe(
      'Infrastructure leads back to this score (a cycle).',
    )
    expect(serverConfigMessage('input_max_required: business_value_chf')).toBe(
      'business_value_chf has no maximum: give it one, or a cap (rescaling needs it).',
    )
    expect(serverConfigMessage('the score impact_score would become invalid: cycle (x)')).toBe(
      'the score impact_score would become invalid: cycle (x)',
    )
  })
})
