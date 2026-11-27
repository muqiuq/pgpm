import { describe, expect, it } from 'vitest'
import { buildCatalogue } from '@/local/catalogue'
import { fields, sections } from '@/test/configFixture'
import {
  dateTimeFromLocal,
  fieldWidget,
  fromDraft,
  isEmptyValue,
  pickableOptions,
  ratingScale,
  subheadingOf,
  toDraft,
  valueError,
  type FieldLike,
} from './fieldWidget'
import {
  REMOVED_OPTION,
  renderValue,
  scoreText,
  searchText,
  sortValueOf,
  timeboxPassed,
  timeboxReason,
} from './renderValue'

const catalogue = buildCatalogue(sections, fields)
const f = (key: string) => catalogue.fieldByKey.get(key)!
const like = (
  type: FieldLike['type'],
  config: FieldLike['config'] = {},
  extra: Partial<FieldLike> = {},
): FieldLike => ({
  key: 'x',
  label: 'X',
  type,
  config,
  ...extra,
})

describe('field widgets (docs/01 §6.1, docs/03 §12.7)', () => {
  it('picks one widget per type: inline editors, one-click ratings, Markdown pages, read-only scores', () => {
    expect(fieldWidget({ type: 'text' }).inline).toBe('text')
    expect(fieldWidget({ type: 'textarea' }).inline).toBe('textarea')
    expect(fieldWidget({ type: 'number' }).inline).toBe('number')
    expect(fieldWidget({ type: 'date' }).inline).toBe('date')
    expect(fieldWidget({ type: 'bool' }).inline).toBe('bool')
    expect(fieldWidget({ type: 'select' }).inline).toBe('enum')
    expect(fieldWidget({ type: 'multiselect' })).toMatchObject({ inline: 'custom', list: true })
    expect(fieldWidget({ type: 'person' })).toMatchObject({ inline: 'custom', list: true })
    expect(fieldWidget({ type: 'markdown' })).toMatchObject({ page: true, inline: null })
    expect(fieldWidget({ type: 'rating' })).toMatchObject({ oneClick: true, inline: null })
    expect(fieldWidget({ type: 'score' })).toMatchObject({ computed: true, inline: null })
    expect(subheadingOf(f('feas_data_access'))).toBe('Data & infrastructure')
    expect(subheadingOf(f('feas_infra'))).toBeNull()
    expect(ratingScale(f('feas_infra').config)).toEqual({
      min: 0,
      max: 5,
      allowNa: true,
      labels: { '0': 'does not apply', '5': 'fully applies' },
    })
    expect(ratingScale(null)).toMatchObject({ min: 0, max: 5, allowNa: false })
  })

  it('checks values as the server does (FieldValueRules), null fitting unless required', () => {
    expect(valueError(like('text', { maxLength: 3 }), 'abcd')).toBe('At most 3 characters.')
    expect(valueError(like('text'), 12)).toBe('A text.')
    expect(valueError(like('url'), 'ftp://x')).toBe('An http(s) or mailto address.')
    expect(valueError(like('url'), 'https://example.com/a')).toBeNull()
    expect(valueError(like('email'), 'no-mail')).toBe('An e-mail address.')
    expect(valueError(f('business_value_chf'), -1)).toBe('Outside the field’s min/max.')
    expect(valueError(f('business_value_chf'), '12')).toBe('A number.')
    expect(valueError(like('date'), '2026-02-30')).toBe('A date yyyy-MM-dd.')
    expect(valueError(like('date'), '2026-02-28')).toBeNull()
    expect(valueError(like('datetime'), '2026-11-26T14:00:00Z')).toBeNull()
    expect(valueError(like('datetime'), '2026-11-26 14:00')).toContain('UTC timestamp')
    expect(valueError(like('bool'), 'yes')).toBe('true or false.')
    expect(valueError(f('time_to_proof_of_value'), 'lt3')).toBeNull()
    expect(valueError(f('time_to_proof_of_value'), 'nope')).toContain('options')
    expect(valueError(f('strategic_advantages'), ['efficiency', 'efficiency'])).toBe('Each option once.')
    expect(valueError(f('strategic_advantages'), ['efficiency', 'nope'])).toBe('Unknown option.')
    const person = '01970000-0000-7000-8000-000000000001'
    const other = '01970000-0000-7000-8000-000000000002'
    expect(valueError(f('data_owner'), [person])).toBeNull()
    expect(valueError(f('data_owner'), [person, other])).toBe('One person (the field takes one).')
    expect(valueError(f('data_owner'), ['not-a-uuid'])).toBe('A list of person ids.')
    expect(valueError(f('feas_infra'), 'na')).toBeNull()
    expect(valueError(like('rating'), 'na')).toBe('n/a is not allowed for this field.')
    expect(valueError(f('feas_infra'), 6)).toBe('An integer from 0 to 5 or "na".')
    expect(valueError(f('feas_infra'), 2.5)).toContain('An integer')
    expect(valueError(f('feasibility_index'), 3)).toBe('Score values are computed, never stored.')
    expect(valueError(like('text', {}, { required: true }), '  ')).toBe('The field is required.')
    expect(valueError(like('text'), null)).toBeNull()
    expect(isEmptyValue([])).toBe(true)
    expect(isEmptyValue(0)).toBe(false)
  })

  it('turns an editor’s draft into the stored value and back', () => {
    expect(fromDraft(like('text'), '  Hello  ')).toBe('Hello')
    expect(fromDraft(like('text'), '   ')).toBeNull()
    expect(fromDraft(like('textarea'), 'line 1\nline 2\n\n')).toBe('line 1\nline 2')
    expect(fromDraft(like('number', { decimals: 1 }), 2.26)).toBe(2.3)
    expect(fromDraft(like('number'), '')).toBeNull()
    expect(fromDraft(like('date'), '')).toBeNull()
    expect(fromDraft(like('bool'), true)).toBe(true)
    expect(toDraft(like('bool'), null)).toBe(false)
    expect(toDraft(like('number'), 5)).toBe(5)
    expect(toDraft(like('text'), null)).toBeNull()
    expect(dateTimeFromLocal('')).toBeNull()
    expect(dateTimeFromLocal('2026-11-26T14:00')).toMatch(/^2026-11-26T\d{2}:00:00Z$/)
    expect(pickableOptions(f('strategic_advantages').config).map((o) => o.id)).toEqual(['efficiency', 'quality'])
    expect(pickableOptions(f('strategic_advantages').config, ['old']).map((o) => o.id)).toContain('old')
  })

  it('renders values: option chips with points, removed options, ratings, scores, persons, timeboxes', () => {
    const select = renderValue(f('time_to_proof_of_value'), 'lt3')
    expect(select.text).toBe('< 3 months (+5)')
    expect(select.options![0]).toMatchObject({ label: '< 3 months', color: '#16a34a', points: 5, removed: false })
    expect(renderValue(f('time_to_proof_of_value'), 'gone').text).toBe(REMOVED_OPTION)
    const multi = renderValue(f('strategic_advantages'), ['quality', 'gone', 'efficiency'])
    expect(multi.options!.map((o) => o.label)).toEqual(['Efficiency', 'Quality', REMOVED_OPTION])
    expect(renderValue(f('feas_infra'), 4).text).toBe('4 / 5')
    expect(renderValue(f('feas_infra'), 0).text).toBe('0 / 5 (does not apply)')
    expect(renderValue(f('feas_infra'), 'na')).toMatchObject({ text: 'n/a', na: true })
    expect(renderValue(f('feasibility_index'), 58).text).toBe('58 / 65')
    expect(renderValue(f('feasibility_index'), null).empty).toBe(true)
    expect(scoreText(null, 65)).toBe('–')
    expect(renderValue(f('business_value_chf'), 15000).text).toMatch(/^15.000 CHF \/ year$/)
    expect(renderValue(f('is_ai_system'), null).text).toBe('No')
    expect(renderValue(f('data_owner'), ['p1'], { personName: (id) => (id === 'p1' ? 'Ana' : undefined) }).text).toBe(
      'Ana',
    )
    expect(renderValue(f('data_owner'), ['p2']).text).toBe('Unknown person')
    expect(searchText(f('strategic_advantages'), ['efficiency'])).toBe('Efficiency')
    expect(searchText(f('feas_infra'), 4)).toBe('')
    expect(sortValueOf(f('time_to_proof_of_value'), 'gt12')).toBe(2)
    expect(sortValueOf(f('feas_infra'), 'na')).toBe(-1)
    expect(sortValueOf(f('feasibility_index'), null)).toBeNull()
    expect(timeboxPassed(f('timebox_end'), '2026-11-01', '2026-11-27', false)).toBe(true)
    expect(timeboxPassed(f('timebox_end'), '2026-11-01', '2026-11-27', true)).toBe(false)
    expect(timeboxPassed(f('timebox_end'), '2026-12-01', '2026-11-27', false)).toBe(false)
    expect(timeboxPassed(like('date'), '2026-11-01', '2026-11-27', false)).toBe(false)
    const boxes = [f('timebox_end')]
    expect(timeboxReason(boxes, { timebox_end: '2026-11-01' }, '2026-11-27', false)).toMatch(/^End of timebox passed /)
    expect(timeboxReason(boxes, { timebox_end: '2026-11-01' }, '2026-11-27', true)).toBeNull()
    expect(timeboxReason(boxes, {}, '2026-11-27', false)).toBeNull()
  })
})
