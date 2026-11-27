import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { buildCatalogue } from '@/local/catalogue'
import { fields, sections } from '@/test/configFixture'
import { until } from '@/test/memoryDb'
import { mountWithUi } from '@/test/mount'
import FieldEditor from './FieldEditor.vue'
import FieldValue from './FieldValue.vue'
import RatingInput from './RatingInput.vue'
import ScoreBadge from './ScoreBadge.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }), useRouter: () => ({ push, replace: vi.fn() }) }))

const catalogue = buildCatalogue(sections, fields)
const f = (key: string) => catalogue.fieldByKey.get(key)!

describe('the field widgets (docs/03 §12.7)', () => {
  const mounted: { unmount(): void }[] = []
  beforeEach(() => {
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
  })
  afterEach(() => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
  })

  it('RatingInput: 0–5 and n/a, one click sets, the chosen one again clears; labels as tooltips', async () => {
    const w = mountWithUi(RatingInput, { props: { config: f('feas_infra').config, label: 'Infra', modelValue: null } })
    const buttons = w.findAll('button')
    expect(buttons.map((b) => b.text())).toEqual(['0', '1', '2', '3', '4', '5', 'n/a'])
    expect(buttons[0]!.attributes('title')).toBe('does not apply')
    expect(buttons[5]!.attributes('aria-label')).toBe('Infra: fully applies')
    await buttons[4]!.trigger('click')
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([4])
    await w.setProps({ modelValue: 4 })
    expect(w.findAll('button')[4]!.attributes('aria-pressed')).toBe('true')
    await w.findAll('button')[4]!.trigger('click')
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([null])
    await w.findAll('button')[6]!.trigger('click')
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['na'])
    await w.setProps({ disabled: true })
    await w.findAll('button')[2]!.trigger('click')
    expect(w.emitted('update:modelValue')).toHaveLength(3)
  })

  it('ScoreBadge and FieldValue: "58 / 65", incomplete, option chips with points, removed options', () => {
    const score = mountWithUi(ScoreBadge, { props: { value: 58, max: 65, label: 'Feasibility index' } })
    expect(score.text()).toBe('58 / 65')
    expect(score.attributes('aria-label')).toBe('Feasibility index: 58 of 65')
    const incomplete = mountWithUi(ScoreBadge, { props: { value: null, max: 65 } })
    expect(incomplete.text()).toContain('incomplete')
    const tinted = mountWithUi(ScoreBadge, {
      props: {
        value: 58,
        max: 65,
        thresholds: [
          { at: 0.5, color: '#16a34a' },
          { at: 0.9, color: '#0000ff' },
        ],
      },
    })
    expect(tinted.attributes('style')).toContain('#16a34a')

    const chips = mountWithUi(FieldValue, { props: { field: f('time_to_proof_of_value'), value: 'lt3' } })
    expect(chips.text()).toBe('< 3 months +5')
    const removed = mountWithUi(FieldValue, {
      props: { field: f('strategic_advantages'), value: ['efficiency', 'gone'] },
    })
    expect(removed.findAll('.chip').map((c) => c.text())).toEqual(['Efficiency', '(removed option)'])
    expect(removed.find('.chip.removed').exists()).toBe(true)
    const rated = mountWithUi(FieldValue, { props: { field: f('feas_infra'), value: 'na' } })
    expect(rated.text()).toBe('n/a')
    const late = mountWithUi(FieldValue, {
      props: { field: f('timebox_end'), value: '2026-11-01', overdue: 'End of timebox passed' },
    })
    expect(late.text()).toContain('Overdue')
    const empty = mountWithUi(FieldValue, { props: { field: f('business_value_chf'), value: null } })
    expect(empty.text()).toBe('–')
  })

  it('FieldEditor: saves a number inline; a refused value stays with its reason; read-only shows the value', async () => {
    const save = vi.fn(async () => {})
    const w = mountWithUi(FieldEditor, {
      props: { field: f('business_value_chf'), value: null, save },
      attachTo: document.body,
    })
    mounted.push(w)
    await w.get('button[aria-label="Edit Estimated business value"]').trigger('click')
    const input = w.get('input')
    await input.setValue('-5')
    await w.get('button[aria-label="Save Estimated business value"]').trigger('click')
    await until(() => w.text().includes('Outside the field’s min/max.'), 'the reason')
    expect(save).not.toHaveBeenCalled()
    await w.get('input').setValue('15000')
    await w.get('button[aria-label="Save Estimated business value"]').trigger('click')
    await until(() => save.mock.calls.length === 1, 'the save')
    expect(save).toHaveBeenCalledWith(15000)

    const ro = mountWithUi(FieldEditor, {
      props: { field: f('rb_decision'), value: 'go', save, readonly: 'Only some groups may edit it.' },
    })
    expect(ro.text()).toContain('Go')
    expect(ro.find('[role="combobox"]').exists()).toBe(false)
    expect(ro.find('button').exists()).toBe(false)
  })

  it('FieldEditor: ratings save on click, Markdown opens its editing page, scores are never editable', async () => {
    const save = vi.fn(async () => {})
    const rating = mountWithUi(FieldEditor, { props: { field: f('feas_infra'), value: 3, save } })
    expect(rating.get('[aria-label="Statement 2: 3"]').attributes('aria-pressed')).toBe('true')
    await rating.get('[aria-label="Statement 2: fully applies"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenLastCalledWith(5)

    const md = mountWithUi(FieldEditor, {
      props: { field: f('notes_md'), value: '# Heading\n\nText', save, itemType: 'project', itemId: 'p1' },
    })
    expect(md.text()).toContain('Heading')
    await md.get('button[aria-label="Edit Notes"]').trigger('click')
    expect(push).toHaveBeenCalledWith({ name: 'project-field-edit', params: { id: 'p1', key: 'notes_md' } })

    const score = mountWithUi(FieldEditor, { props: { field: f('feasibility_index'), value: 58, save } })
    expect(score.text()).toBe('58 / 65')
    expect(score.find('button').exists()).toBe(false)
  })
})
