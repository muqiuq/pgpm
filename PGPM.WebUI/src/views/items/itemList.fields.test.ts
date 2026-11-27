import { afterEach, describe, expect, it } from 'vitest'
import { buildCatalogue } from '@/local/catalogue'
import type { ItemRow, ItemType } from '@/local/rows'
import { fields, playbookRatings, sections, statuses } from '@/test/configFixture'
import {
  columnsFor,
  emptyContext,
  itemFieldValue,
  itemText,
  matchItem,
  registerColumns,
  sortValue,
  statusOf,
  visibleColumns,
  type ListContext,
} from './itemList'

const catalogue = buildCatalogue(sections, fields)
const at = '2026-11-01T00:00:00Z'
const now = Date.parse('2026-11-27T12:00:00Z')

function item(id: string, type: ItemType, statusId: string | null, extra: Partial<ItemRow> = {}): ItemRow {
  return {
    id,
    seq: 1,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: at,
    createdBy: null,
    updatedAt: at,
    updatedBy: null,
    type,
    number: Number(id.replace(/\D/g, '')) || 1,
    title: `Item ${id}`,
    description: null,
    color: null,
    imageFileId: null,
    ownerUserId: 'u1',
    statusId,
    statusChangedAt: at,
    tags: [],
    originItemId: null,
    convertedToItemId: null,
    pinned: false,
    leadOrganisation: null,
    leadValue: null,
    leadCurrency: null,
    leadProbability: null,
    leadDecisionDate: null,
    leadNextAction: null,
    leadNextActionDue: null,
    leadSource: null,
    projectTemplateId: null,
    projectStartDate: null,
    projectEndDate: null,
    access: 'edit',
    ...extra,
  }
}

const complete = { ...playbookRatings, time_to_proof_of_value: 'lt3' }
const ideas = [item('i1', 'idea', 's-idea-ready'), item('i2', 'idea', 's-idea-ready'), item('i3', 'idea', 's-idea-new')]
const projects = [
  item('p1', 'project', 's-project-poc', { statusChangedAt: '2026-08-01T00:00:00Z' }),
  item('p2', 'project', 's-project-poc', { statusChangedAt: '2026-11-20T00:00:00Z' }),
  item('p3', 'project', 's-project-stopped'),
]

const ctx: ListContext = emptyContext({
  statuses: new Map(statuses.map((s) => [s.id, s])),
  catalogue,
  now,
  fieldValues: new Map<string, Record<string, unknown>>([
    ['i1', { ...complete, strategic_advantages: ['efficiency', 'quality'], business_value_chf: 15000 }],
    ['i2', { ...complete, feas_data_access: 'na', feas_solution_known: 'na', strategic_advantages: ['gone'] }],
    ['i3', { feas_infra: 2 }],
    ['p1', { timebox_end: '2026-11-01', is_ai_system: true, scope: 'Invoices of the finance department' }],
    ['p2', { timebox_end: '2027-01-15', is_ai_system: false }],
    ['p3', { timebox_end: '2026-10-01', is_ai_system: true }],
  ]),
  persons: new Map(),
})

const pick = (rows: ItemRow[], filters: Parameters<typeof matchItem>[1]) =>
  rows.filter((r) => matchItem(r, filters, ctx)).map((r) => r.id)

describe('item lists with dynamic fields (docs/01 §4.3, docs/03 §12.7)', () => {
  let unregister: (() => void) | null = null
  afterEach(() => unregister?.())

  it('offers a column per field of the type’s sections – scores included – and keeps a view’s field columns', () => {
    const ids = columnsFor('idea', ctx).map((c) => c.id)
    expect(ids).toEqual(expect.arrayContaining(['status', 'field:feasibility_index', 'field:rb_decision']))
    expect(ids).not.toContain('field:timebox_end')
    expect(columnsFor('project', ctx).map((c) => c.id)).toContain('field:timebox_end')
    expect(columnsFor('project', ctx).map((c) => c.id)).not.toContain('field:rb_decision')
    expect(columnsFor('note', ctx).some((c) => c.id.startsWith('field:'))).toBe(false)
    expect(
      visibleColumns('idea', ['field:feasibility_index', 'field:gone', 'status'], ctx).map((c) => [c.id, c.label]),
    ).toEqual([
      ['key', 'Key'],
      ['title', 'Title'],
      ['field:feasibility_index', 'Feasibility index'],
      ['status', 'Status'],
    ])
  })

  it('computes scores per item, sorts by them (incomplete last) and searches option labels', () => {
    const index = catalogue.fieldByKey.get('feasibility_index')!
    expect(ideas.map((i) => itemFieldValue(i, index, ctx))).toEqual([58, 57, null])
    expect(sortValue(ideas[0]!, 'field:feasibility_index', ctx)).toBe(58)
    expect(sortValue(ideas[2]!, 'field:feasibility_index', ctx)).toBeNull()
    expect(sortValue(ideas[0]!, 'field:time_to_proof_of_value', ctx)).toBe(0)
    expect(itemText(ideas[0]!, ctx)).toContain('Efficiency')
    expect(itemText(projects[0]!, ctx)).toContain('finance department')
    expect(itemText(ideas[0]!)).not.toContain('Efficiency')
  })

  it('filters on fields: range of a score, eq of a switch, in of options, contains, set / unset', () => {
    const all = [...ideas, ...projects]
    expect(pick(all, { fields: [{ field: 'field:feasibility_index', op: 'range', min: 58 }] })).toEqual(['i1'])
    expect(pick(all, { fields: [{ field: 'field:feasibility_index', op: 'range', min: 50, max: 57 }] })).toEqual(['i2'])
    expect(pick(all, { fields: [{ field: 'field:feasibility_index', op: 'unset' }] })).toEqual(['i3', 'p1', 'p2', 'p3'])
    expect(pick(all, { fields: [{ field: 'field:is_ai_system', op: 'eq', value: true }] })).toEqual(['p1', 'p3'])
    expect(pick(all, { fields: [{ field: 'field:strategic_advantages', op: 'in', values: ['quality'] }] })).toEqual([
      'i1',
    ])
    expect(pick(all, { fields: [{ field: 'field:scope', op: 'contains', value: 'FINANCE' }] })).toEqual(['p1'])
    expect(pick(all, { fields: [{ field: 'field:feas_infra', op: 'eq', value: 2 }] })).toEqual(['i3'])
    expect(pick(all, { fields: [{ field: 'field:timebox_end', op: 'range', max: '2026-12-31' }] })).toEqual([
      'p1',
      'p3',
    ])
    expect(
      pick(all, {
        fields: [
          { field: 'field:is_ai_system', op: 'set' },
          { field: 'field:timebox_end', op: 'range', min: '2026-10-15' },
        ],
      }),
    ).toEqual(['p1', 'p2'])
  })

  it('marks a passed timebox overdue (not when closed): Running experiments by status, days in status', () => {
    expect(statusOf(projects[0]!, ctx).overdue).toContain('118 days in Prototype / PoC')
    expect(statusOf(projects[1]!, ctx).overdue).toBeNull()
    // p3 is Stopped (closed): its passed timebox doesn't count.
    expect(pick(projects, { overdue: true })).toEqual(['p1'])
    const late = { ...ctx, fieldValues: new Map([['p2', { timebox_end: '2026-11-26' }]]) }
    expect(statusOf(projects[1]!, late).overdue).toMatch(/^End of timebox passed \(.+\)$/)
    expect(matchItem(projects[1]!, { overdue: true }, late)).toBe(true)
    // The seeded *Running experiments* view: projects in Prototype / PoC, longest in it first.
    const running = projects.filter((p) => matchItem(p, { statusIds: ['s-project-poc'] }, ctx))
    const days = (p: ItemRow) => Number(sortValue(p, 'days_in_status', ctx))
    expect(running.sort((a, b) => days(b) - days(a)).map((p) => p.id)).toEqual(['p1', 'p2'])
    expect(sortValue(projects[0]!, 'field:timebox_end', ctx)).toBeTruthy()
  })

  it('takes further columns through the hook (phase 5: quadrant)', () => {
    unregister = registerColumns({
      columns: (type) => (type === 'idea' ? [{ id: 'quadrant', label: 'Quadrant', width: '9rem' }] : []),
      value: (row, id) => (id === 'quadrant' ? `Q-${row.id}` : undefined),
    })
    expect(columnsFor('idea', ctx).map((c) => c.id)).toContain('quadrant')
    expect(columnsFor('project', ctx).map((c) => c.id)).not.toContain('quadrant')
    expect(sortValue(ideas[0]!, 'quadrant', ctx)).toBe('Q-i1')
    unregister()
    unregister = null
    expect(columnsFor('idea', ctx).map((c) => c.id)).not.toContain('quadrant')
  })
})
