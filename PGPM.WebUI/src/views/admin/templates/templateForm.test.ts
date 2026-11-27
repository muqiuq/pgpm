import { describe, expect, it } from 'vitest'
import { buildTemplates } from '@/local/catalogue'
import type {
  ProjectTemplateRow,
  TemplateFieldDefaultRow,
  TemplateMilestoneRow,
  TemplateSectionRow,
  TemplateStatusRow,
} from '@/local/rows'
import {
  emptyTemplateForm,
  formFromParts,
  newMilestone,
  offsetText,
  templateErrors,
  templateInput,
} from './templateForm'

const synced = {
  seq: 1,
  rev: 1,
  deleted: false,
  deleteBatchId: null,
  createdAt: '',
  createdBy: null,
  updatedAt: '',
  updatedBy: null,
}
const template: ProjectTemplateRow = {
  ...synced,
  id: 't1',
  name: 'AI use case',
  description: 'From the test to operation',
  color: '#8b5cf6',
  icon: 'pi-microchip-ai',
  defaultStatusId: 's-prep',
  defaultShares: [{ groupId: 'g-innovation', access: 'edit' }],
  sortOrder: 20,
  isArchived: false,
}
const statuses: TemplateStatusRow[] = ['s-prep', 's-poc', 's-stopped'].map((statusId, i) => ({
  ...synced,
  id: `ts${i}`,
  templateId: 't1',
  statusId,
}))
const sections: TemplateSectionRow[] = [
  { ...synced, id: 'tsec2', templateId: 't1', sectionId: 'sec-charter', sortOrder: 20 },
  { ...synced, id: 'tsec1', templateId: 't1', sectionId: 'sec-profile', sortOrder: 10 },
]
const defaults: TemplateFieldDefaultRow[] = [{ ...synced, id: 'td1', templateId: 't1', fieldId: 'f-ai', value: true }]
const milestones: TemplateMilestoneRow[] = [
  {
    ...synced,
    id: 'g1',
    templateId: 't1',
    kind: 'gate',
    title: 'PoC decision',
    description: null,
    offsetDays: 56,
    dod: ['Result documented'],
    onGoStatusId: 's-poc',
    onNoGoStatusId: 's-stopped',
    sortOrder: 'a1',
  },
  {
    ...synced,
    id: 'g0',
    templateId: 't1',
    kind: 'gate',
    title: 'Ready for test',
    description: 'Key persons named',
    offsetDays: 0,
    dod: [],
    onGoStatusId: 's-poc',
    onNoGoStatusId: null,
    sortOrder: 'a0',
  },
]
const parts = buildTemplates({ templates: [template], statuses, sections, defaults, milestones }).byId.get('t1')!

const fields = new Map([
  ['f-ai', { key: 'is_ai_system', label: 'AI system', type: 'bool' as const, sectionId: 'sec-profile' }],
  [
    'f-band',
    {
      key: 'band',
      label: 'Band',
      type: 'select' as const,
      sectionId: 'sec-charter',
      config: { options: [{ id: 'a' }] },
    },
  ],
  ['f-other', { key: 'other', label: 'Other', type: 'text' as const, sectionId: 'sec-elsewhere' }],
])

describe('the template editor (docs/01 §10.2, docs/03 §3.3)', () => {
  it('builds the complete request from a synced template: lists in order, milestones with their ids', () => {
    const input = templateInput(formFromParts(parts))
    expect(input).toEqual({
      name: 'AI use case',
      description: 'From the test to operation',
      color: '#8b5cf6',
      icon: 'pi-microchip-ai',
      defaultStatusId: 's-prep',
      statusIds: ['s-prep', 's-poc', 's-stopped'],
      sectionIds: ['sec-profile', 'sec-charter'],
      fieldDefaults: [{ fieldId: 'f-ai', value: true }],
      defaultShares: [{ groupId: 'g-innovation', access: 'edit' }],
      milestones: [
        {
          id: 'g0',
          kind: 'gate',
          title: 'Ready for test',
          description: 'Key persons named',
          offsetDays: 0,
          dod: [],
          onGoStatusId: 's-poc',
          onNoGoStatusId: null,
        },
        {
          id: 'g1',
          kind: 'gate',
          title: 'PoC decision',
          description: null,
          offsetDays: 56,
          dod: ['Result documented'],
          onGoStatusId: 's-poc',
          onNoGoStatusId: 's-stopped',
        },
      ],
    })
  })

  it('sends new milestones without an id, trims texts, drops empty criteria, suggestions for gates only', () => {
    const form = emptyTemplateForm()
    form.name = '  Workshop '
    form.icon = 'pi pi-bolt'
    form.milestones = [
      { ...newMilestone('milestone', 14), title: ' Kick-off ', dod: ['Room booked', '  '], onGoStatusId: 's-x' },
    ]
    const input = templateInput(form)
    expect(input).toMatchObject({ name: 'Workshop', icon: 'pi-bolt', description: null, color: null })
    expect(input.milestones).toEqual([
      {
        kind: 'milestone',
        title: 'Kick-off',
        description: null,
        offsetDays: 14,
        dod: ['Room booked'],
        onGoStatusId: null,
        onNoGoStatusId: null,
      },
    ])
  })

  it('validates what it can: name, default and suggested statuses allowed, defaults fitting their fields', () => {
    const form = formFromParts(parts)
    expect(templateErrors(form, { fieldById: fields })).toEqual({})
    form.name = ' '
    form.statusIds = ['s-prep']
    form.fieldDefaults = [
      { fieldId: 'f-ai', value: null },
      { fieldId: 'f-band', value: 'zz' },
      { fieldId: 'f-other', value: 'x' },
      { fieldId: 'f-gone', value: 1 },
    ]
    form.milestones[0]!.title = ''
    expect(templateErrors(form, { fieldById: fields })).toEqual({
      name: 'A name is needed.',
      'fieldDefaults.f-ai': 'A value is needed.',
      'fieldDefaults.f-band': 'One of the field’s options (its id).',
      'fieldDefaults.f-other': 'Its section is not part of the template.',
      'fieldDefaults.f-gone': 'The field is gone.',
      'milestones.0.title': 'A title is needed.',
      'milestones.0.onGoStatusId': 'Not among the allowed statuses.',
      'milestones.1.onGoStatusId': 'Not among the allowed statuses.',
      'milestones.1.onNoGoStatusId': 'Not among the allowed statuses.',
    })
  })

  it('reads offsets as days or weeks from the start', () => {
    expect(offsetText(0)).toBe('At the start')
    expect(offsetText(56)).toBe('+8 weeks')
    expect(offsetText(7)).toBe('+1 week')
    expect(offsetText(10)).toBe('+10 days')
    expect(offsetText(1)).toBe('+1 day')
    expect(offsetText(-3)).toBe('−3 days')
    expect(offsetText(null)).toBe('No due date')
  })
})
