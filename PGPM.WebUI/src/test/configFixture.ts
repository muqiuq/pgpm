import type { FieldConfig, FieldType } from '@/domain/score'
import type { PgpmDb } from '@/local/db'
import type {
  FieldDefinitionRow,
  ProjectTemplateRow,
  SectionRow,
  StatusRow,
  TemplateFieldDefaultRow,
  TemplateMilestoneRow,
  TemplateSectionRow,
  TemplateStatusRow,
} from '@/local/rows'

/**
 * A small configuration after the `innovation-ai` package (docs/04 §4.2) for the client tests: idea and project
 * statuses, the sections *Feasibility* (the 12 ratings, the time to proof of value with points, the feasibility index),
 * *Value* (a multiselect, a number), *Review board decision* (ideas, editable by the review board only – `canEdit` false
 * for the test user), *Charter* (projects, a timebox date) and *Data & platform* (projects, a person), and the template
 * *AI use case* with its statuses, sections, defaults, shares and gates.
 */

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, createdBy: null, updatedBy: null }
const at = '2026-11-01T00:00:00Z'
const base = { ...synced, createdAt: at, updatedAt: at }

export const G_TEAM = '01970000-0000-7000-8000-0000000000a1'
export const G_BOARD = '01970000-0000-7000-8000-0000000000a2'

function status(id: string, itemType: StatusRow['itemType'], name: string, extra: Partial<StatusRow> = {}): StatusRow {
  return {
    ...base,
    id,
    itemType,
    name,
    color: '#64748b',
    icon: null,
    category: 'open',
    sortOrder: 10,
    isDefault: false,
    maxDays: null,
    isArchived: false,
    ...extra,
  }
}

export const statuses: StatusRow[] = [
  status('s-idea-new', 'idea', 'New', { isDefault: true, sortOrder: 10 }),
  status('s-idea-ready', 'idea', 'Ready for review', { sortOrder: 40 }),
  status('s-idea-selected', 'idea', 'Selected', { sortOrder: 50, category: 'closed' }),
  status('s-project-draft', 'project', 'Draft', { isDefault: true, sortOrder: 10 }),
  status('s-project-active', 'project', 'Active', { sortOrder: 20, category: 'active' }),
  status('s-project-preparing', 'project', 'Preparing test', { sortOrder: 30 }),
  status('s-project-poc', 'project', 'Prototype / PoC', { sortOrder: 40, category: 'active', maxDays: 84 }),
  status('s-project-mvp', 'project', 'MVP / pilot', { sortOrder: 50, category: 'active' }),
  status('s-project-stopped', 'project', 'Stopped', { sortOrder: 90, category: 'closed' }),
]

function section(id: string, name: string, sortOrder: number, extra: Partial<SectionRow> = {}): SectionRow {
  return {
    ...base,
    id,
    name,
    description: null,
    appliesTo: ['idea', 'project'],
    defaultFor: ['idea'],
    sortOrder,
    collapsedByDefault: false,
    isArchived: false,
    canEdit: true,
    ...extra,
  }
}

export const sections: SectionRow[] = [
  section('sec-value', 'Value', 20),
  section('sec-feas', 'Feasibility', 30, { description: 'Twelve statements rated 0–5.' }),
  section('sec-rb', 'Review board decision', 60, { appliesTo: ['idea'], canEdit: false }),
  section('sec-charter', 'Charter', 90, { appliesTo: ['project'], defaultFor: [] }),
  section('sec-data', 'Data & platform', 70, { appliesTo: ['project'], defaultFor: [], collapsedByDefault: true }),
  section('sec-old', 'Old section', 99, { isArchived: true }),
]

let order = 0
function field(
  sectionId: string,
  key: string,
  label: string,
  type: FieldType,
  config: FieldConfig = {},
  extra: Partial<FieldDefinitionRow> = {},
): FieldDefinitionRow {
  order += 10
  return {
    ...base,
    id: `f-${key}`,
    sectionId,
    key,
    label,
    helpText: null,
    type,
    config,
    required: false,
    defaultValue: null,
    sortOrder: order,
    isArchived: false,
    ...extra,
  }
}

export const feasibilityKeys = [
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
] as const

/** The playbook's example (p. 34): 53 rating points + 5 for < 3 months = 58 of 65. */
export const playbookRatings: Record<(typeof feasibilityKeys)[number], number> = {
  feas_data_access: 5,
  feas_infra: 4,
  feas_data_quality: 4,
  feas_solution_known: 5,
  feas_similar_built: 4,
  feas_technologies: 5,
  feas_no_new_processes: 5,
  feas_processes_adaptable: 4,
  feas_no_major_changes: 4,
  feas_tech_knowhow: 4,
  feas_domain_knowhow: 5,
  feas_ai_understanding: 4,
}

const rating: FieldConfig = { min: 0, max: 5, allowNa: true, labels: { '0': 'does not apply', '5': 'fully applies' } }

export const fields: FieldDefinitionRow[] = [
  field('sec-value', 'strategic_advantages', 'Strategic advantages', 'multiselect', {
    options: [
      { id: 'efficiency', label: 'Efficiency', color: '#16a34a' },
      { id: 'quality', label: 'Quality' },
      { id: 'old', label: 'Old one', archived: true },
    ],
    multiPoints: 'count',
  }),
  field('sec-value', 'business_value_chf', 'Estimated business value', 'number', {
    decimals: 0,
    min: 0,
    unit: 'CHF / year',
  }),
  ...feasibilityKeys.map((key, i) =>
    field(
      'sec-feas',
      key,
      `Statement ${i + 1}`,
      'rating',
      i === 0 ? { ...rating, subheading: 'Data & infrastructure' } : rating,
    ),
  ),
  field('sec-feas', 'time_to_proof_of_value', 'Time to a verified proof of value', 'select', {
    options: [
      { id: 'lt3', label: '< 3 months', color: '#16a34a', points: 5 },
      { id: 'm4_6', label: '4–6 months', points: 4 },
      { id: 'gt12', label: '> 12 months', points: 1 },
    ],
  }),
  field('sec-feas', 'feasibility_index', 'Feasibility index', 'score', {
    op: 'sum',
    inputs: [...feasibilityKeys, 'time_to_proof_of_value'],
    na: 'rescale',
    max: 65,
    decimals: 0,
  }),
  field('sec-rb', 'rb_decision', 'Decision', 'select', {
    options: [
      { id: 'go', label: 'Go', color: '#16a34a' },
      { id: 'no_go', label: 'No-go', color: '#dc2626' },
    ],
  }),
  field('sec-charter', 'timebox_end', 'End of timebox', 'date', { timebox: true }),
  field('sec-charter', 'scope', 'Scope', 'textarea', {}, { defaultValue: 'To be defined' }),
  field('sec-data', 'data_owner', 'Data owner', 'person', { multiple: false }),
  field('sec-data', 'is_ai_system', 'AI system', 'bool'),
  field('sec-data', 'notes_md', 'Notes', 'markdown'),
]

export const templateId = 'tpl-ai'

export const template: ProjectTemplateRow = {
  ...base,
  id: templateId,
  name: 'AI use case',
  description: 'From the test to operation.',
  color: '#8b5cf6',
  icon: 'pi-microchip-ai',
  defaultStatusId: 's-project-preparing',
  defaultShares: [
    { groupId: G_TEAM, access: 'edit' },
    { groupId: G_BOARD, access: 'edit' },
  ],
  sortOrder: 20,
  isArchived: false,
}

export const templateStatuses: TemplateStatusRow[] = [
  's-project-preparing',
  's-project-poc',
  's-project-mvp',
  's-project-stopped',
].map((statusId, i) => ({ ...base, id: `ts-${i}`, templateId, statusId }))

export const templateSections: TemplateSectionRow[] = ['sec-value', 'sec-feas', 'sec-charter', 'sec-data'].map(
  (sectionId, i) => ({ ...base, id: `tsec-${i}`, templateId, sectionId, sortOrder: (i + 1) * 10 }),
)

export const templateDefaults: TemplateFieldDefaultRow[] = [
  { ...base, id: 'tdef-ai', templateId, fieldId: 'f-is_ai_system', value: true },
]

export const templateMilestones: TemplateMilestoneRow[] = [
  {
    ...base,
    id: 'tm-g0',
    templateId,
    kind: 'gate',
    title: 'Ready for test',
    description: null,
    offsetDays: 0,
    dod: ['Key persons defined', 'Test phase prepared'],
    onGoStatusId: 's-project-poc',
    onNoGoStatusId: 's-project-stopped',
    sortOrder: 'a0',
  },
  {
    ...base,
    id: 'tm-m1',
    templateId,
    kind: 'milestone',
    title: 'Prototype built',
    description: null,
    offsetDays: 28,
    dod: ['Prototype built'],
    onGoStatusId: null,
    onNoGoStatusId: null,
    sortOrder: 'a1',
  },
  {
    ...base,
    id: 'tm-g1',
    templateId,
    kind: 'gate',
    title: 'PoC decision',
    description: null,
    offsetDays: 56,
    dod: ['Result documented', 'Go / no-go decided'],
    onGoStatusId: 's-project-mvp',
    onNoGoStatusId: 's-project-stopped',
    sortOrder: 'a2',
  },
]

/** Puts the configuration into a local database (as the sync would). */
export async function seedConfig(db: PgpmDb, extra: { sections?: SectionRow[] } = {}) {
  const local = { _local: { serverRev: 1, pending: false } }
  const put = (table: string, rows: readonly object[]) =>
    db.synced(table).bulkPut(rows.map((r) => ({ ...r, ...local })) as never[])
  await put('statuses', statuses)
  await put('sections', extra.sections ?? sections)
  await put('field_definitions', fields)
  await put('project_templates', [template])
  await put('template_statuses', templateStatuses)
  await put('template_sections', templateSections)
  await put('template_field_defaults', templateDefaults)
  await put('template_milestones', templateMilestones)
  await put('groups', [
    { ...base, id: G_TEAM, name: 'Innovation team', description: null },
    { ...base, id: G_BOARD, name: 'Review board', description: null },
  ])
}
