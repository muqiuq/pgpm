import type { ShareSpec } from '@/api/sync'
import { addDays } from '@/domain/milestones'
import { defaultSections, loadCatalogue, loadTemplates, type Catalogue, type TemplateParts } from './catalogue'
import { fillDefaults, itemSectionsOf } from './fields'
import { createItem, defaultStatus, statusesOf, today } from './items'
import { addMilestone } from './milestones'
import { inTransaction, type RepoTransaction } from './repo'
import type { ItemRow, ItemSectionRow, SectionRow, StatusRow, Stored } from './rows'

/**
 * Project templates and default sections when an item is created (docs/01 §6.2, §10.2; data model §7.7), on the device
 * – offline too – in the create dialog's batch: a new idea gets the sections *default for ideas*, a project without
 * template those *default for projects*, a project from a template its sections, field defaults, milestones and gates
 * (due = start + offset, the DoD criteria, the gates' status suggestions) and – preselected in the dialog – its default
 * status and shares. The template is applied once; later changes of it don't alter the project.
 */

/** The statuses a project of the template may have: the template's allowed ones (none: every project status). */
export function allowedStatuses(
  all: readonly StatusRow[],
  template: Pick<TemplateParts, 'statusIds'> | null | undefined,
  current?: string | null,
): StatusRow[] {
  const allowed = template?.statusIds.length ? new Set(template.statusIds) : null
  return statusesOf(all, 'project').filter(
    (s) => (!allowed || allowed.has(s.id)) && (!s.isArchived || s.id === current),
  )
}

/** A new project's status: the template's default, else the project default, else the first allowed one. */
export function templateStatus(
  all: readonly StatusRow[],
  template: TemplateParts | null | undefined,
): StatusRow | undefined {
  const allowed = allowedStatuses(all, template)
  const preferred = template?.template.defaultStatusId
  return (
    allowed.find((s) => s.id === preferred) ??
    allowed.find((s) => s.id === defaultStatus(all, 'project')?.id) ??
    allowed.find((s) => s.isDefault) ??
    allowed[0]
  )
}

/** The templates one may create a project from: not archived, in order. */
export function selectableTemplates(parts: readonly TemplateParts[]): TemplateParts[] {
  return parts.filter((p) => !p.template.isArchived)
}

/** The shares a template preselects in the create dialog (`default_shares`), with the groups the device knows. */
export function templateShares(template: TemplateParts | null | undefined, groupIds: ReadonlySet<string>): ShareSpec[] {
  return (template?.template.defaultShares ?? [])
    .filter((s) => groupIds.has(s.groupId))
    .map((s) => ({ groupId: s.groupId, access: s.access }))
}

export interface ItemSetup {
  /** Projects: the template applied (null: blank – the sections default for projects). */
  template?: TemplateParts | null
  /** Projects: the start date the template's milestones count from (default today). */
  startDate?: string | null
  /** Conversion: the source's sections that apply to the new item, and the values to carry (by field id). */
  carried?: { sectionIds: readonly string[]; values: ReadonlyMap<string, unknown> }
}

export interface SetupResult {
  sections: number
  values: number
  milestones: number
}

/**
 * Gives a new idea or project (inside the creating transaction) its sections, their field values (carried ones first,
 * then the template's defaults, then the fields' own) and – from a template – its milestones and gates.
 */
export function setUpItem(
  tx: RepoTransaction,
  item: Pick<ItemRow, 'id' | 'type'>,
  catalogue: Catalogue,
  setup: ItemSetup = {},
): Promise<SetupResult> {
  return inTransaction(setUp(tx, item, catalogue, setup))
}

async function setUp(
  tx: RepoTransaction,
  item: Pick<ItemRow, 'id' | 'type'>,
  catalogue: Catalogue,
  setup: ItemSetup,
): Promise<SetupResult> {
  const result: SetupResult = { sections: 0, values: 0, milestones: 0 }
  if (item.type !== 'idea' && item.type !== 'project') return result
  const type = item.type
  const template = type === 'project' ? (setup.template ?? null) : null
  const fits = (s: SectionRow | undefined): s is SectionRow => !!s && !s.isArchived && s.appliesTo.includes(type)

  const wanted: SectionRow[] = template
    ? template.sections.map((t) => catalogue.sectionById.get(t.sectionId)).filter(fits)
    : defaultSections(catalogue, type)
  for (const id of setup.carried?.sectionIds ?? []) {
    const section = catalogue.sectionById.get(id)
    if (fits(section) && !wanted.some((s) => s.id === id)) wanted.push(section)
  }
  const present = new Set((await itemSectionsOf(tx.db, item.id)).map((s) => s.sectionId))
  let order = 0
  for (const section of wanted) {
    order += 10
    if (present.has(section.id)) continue
    await tx.create<ItemSectionRow>('item_sections', { itemId: item.id, sectionId: section.id, sortOrder: order })
    present.add(section.id)
    result.sections++
  }

  // Carried values of every field that applies to the type (same field = same value), shown or not; then the defaults.
  const values = new Map<string, unknown>()
  for (const d of template?.defaults ?? []) values.set(d.fieldId, d.value)
  for (const [fieldId, value] of setup.carried?.values ?? []) values.set(fieldId, value)
  const carriedSections = [...(setup.carried?.values.keys() ?? [])]
    .map((id) => catalogue.fieldById.get(id))
    .map((f) => (f ? catalogue.sectionById.get(f.sectionId) : undefined))
    .filter(fits)
  const sections = [...wanted, ...carriedSections.filter((s, i, list) => !wanted.includes(s) && list.indexOf(s) === i)]
  result.values = await fillDefaults(tx, item.id, sections, catalogue, values)

  if (template) {
    const start = setup.startDate || today()
    for (const m of template.milestones) {
      await addMilestone(tx, item.id, {
        kind: m.kind,
        title: m.title,
        description: m.description,
        dueDate: m.offsetDays === null || m.offsetDays === undefined ? null : addDays(start, m.offsetDays),
        dod: Array.isArray(m.dod) ? m.dod : [],
        onGoStatusId: m.onGoStatusId,
        onNoGoStatusId: m.onNoGoStatusId,
      })
      result.milestones++
    }
  }
  return result
}

export interface NewConfiguredItem {
  type: ItemRow['type']
  title: string
  statusId?: string | null
  shares: readonly ShareSpec[]
  /** Projects: the template's id (null: blank). */
  templateId?: string | null
  /** Projects: the planned start (anchors the template's milestones; default today with a template). */
  startDate?: string | null
  values?: Partial<ItemRow>
}

/**
 * The create dialog's item (docs/01 §4.4, §10.2) in the caller's transaction: the item and its shares, then its
 * sections, defaults and – from a template – milestones. The template and the catalogue are read from the device.
 */
export function createConfiguredItem(tx: RepoTransaction, input: NewConfiguredItem): Promise<Stored<ItemRow>> {
  return inTransaction(createConfigured(tx, input))
}

async function createConfigured(tx: RepoTransaction, input: NewConfiguredItem): Promise<Stored<ItemRow>> {
  const catalogue = await loadCatalogue(tx.db)
  const templates = input.type === 'project' && input.templateId ? await loadTemplates(tx.db) : null
  const template = input.templateId ? (templates?.byId.get(input.templateId) ?? null) : null
  if (input.templateId && !template) throw new Error('The template is not on this device (any more).')
  const startDate = input.type === 'project' ? input.startDate || (template ? today() : null) : null
  const item = await createItem(tx, {
    type: input.type,
    title: input.title,
    statusId: input.statusId,
    shares: input.shares,
    values: {
      ...input.values,
      ...(input.type === 'project'
        ? { projectTemplateId: template?.template.id ?? null, ...(startDate ? { projectStartDate: startDate } : {}) }
        : {}),
    },
  })
  await setUpItem(tx, item, catalogue, { template, startDate })
  return item
}
