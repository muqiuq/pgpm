import { computed } from 'vue'
import { compareKeyThenId } from '@/domain/fractional'
import type { FieldDef } from '@/domain/score'
import type { PgpmDb } from './db'
import { localDb } from './db'
import { inTransaction } from './repo'
import type {
  FieldDefinitionRow,
  ProjectTemplateRow,
  SectionItemType,
  SectionRow,
  TemplateFieldDefaultRow,
  TemplateMilestoneRow,
  TemplateSectionRow,
  TemplateStatusRow,
} from './rows'
import { useLive } from './useLive'

/**
 * The configuration the item pages, lists, dialogs and admin pages share (docs/01 §6, §10.2; data model §7): the
 * sections the user may see and their fields (the catalogue), and the project templates with their statuses, sections,
 * field defaults and milestones – all synced read-only rows, so they work offline. Pure builders plus live composables.
 */

const live = <T>(rows: readonly T[]): T[] => rows.filter((r) => !(r as { deleted?: boolean }).deleted)

const byOrder = <T extends { sortOrder: number; id: string }>(a: T, b: T) =>
  a.sortOrder - b.sortOrder || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)

export interface Catalogue {
  /** Every live section (archived ones too: their values are still shown where an item has them), in order. */
  sections: SectionRow[]
  /** Every live field definition (archived ones too), by section order, then field order. */
  fields: FieldDefinitionRow[]
  sectionById: ReadonlyMap<string, SectionRow>
  fieldById: ReadonlyMap<string, FieldDefinitionRow>
  fieldByKey: ReadonlyMap<string, FieldDefinitionRow>
  /** A section's fields in order (archived ones too). */
  fieldsBySection: ReadonlyMap<string, FieldDefinitionRow[]>
  /** Every field as score.ts reads it, by key (nested scores, inputs). */
  defsByKey: Readonly<Record<string, FieldDef>>
}

export function buildCatalogue(
  sectionRows: readonly SectionRow[],
  fieldRows: readonly FieldDefinitionRow[],
): Catalogue {
  const sections = live(sectionRows).sort(byOrder)
  const sectionById = new Map(sections.map((s) => [s.id, s]))
  const rank = new Map(sections.map((s, i) => [s.id, i]))
  const fields = live(fieldRows)
    .filter((f) => sectionById.has(f.sectionId))
    .sort((a, b) => rank.get(a.sectionId)! - rank.get(b.sectionId)! || byOrder(a, b))
  const fieldsBySection = new Map<string, FieldDefinitionRow[]>()
  for (const f of fields) {
    const list = fieldsBySection.get(f.sectionId)
    if (list) list.push(f)
    else fieldsBySection.set(f.sectionId, [f])
  }
  const defsByKey: Record<string, FieldDef> = {}
  for (const f of fields) defsByKey[f.key] = { key: f.key, type: f.type, config: f.config, isArchived: f.isArchived }
  return {
    sections,
    fields,
    sectionById,
    fieldById: new Map(fields.map((f) => [f.id, f])),
    fieldByKey: new Map(fields.map((f) => [f.key, f])),
    fieldsBySection,
    defsByKey,
  }
}

export const emptyCatalogue: Catalogue = buildCatalogue([], [])

/** The sections one may add to an item of the type: applying to it, not archived. */
export function sectionsForType(catalogue: Catalogue, type: SectionItemType): SectionRow[] {
  return catalogue.sections.filter((s) => !s.isArchived && s.appliesTo.includes(type))
}

/** The sections a new item of the type gets without a template (`default_for`, data model §7.2). */
export function defaultSections(catalogue: Catalogue, type: SectionItemType): SectionRow[] {
  return sectionsForType(catalogue, type).filter((s) => s.defaultFor.includes(type))
}

/** The catalogue from a database (inside a repository transaction too). */
export function loadCatalogue(db: PgpmDb): Promise<Catalogue> {
  return inTransaction(readCatalogue(db))
}

async function readCatalogue(db: PgpmDb): Promise<Catalogue> {
  const sections = (await db.synced('sections').toArray()) as unknown as SectionRow[]
  const fields = (await db.synced('field_definitions').toArray()) as unknown as FieldDefinitionRow[]
  return buildCatalogue(sections, fields)
}

/** The catalogue, live (follows every configuration change the sync brings). */
export function useCatalogue() {
  const sections = useLive(
    async () => (await localDb().synced('sections').toArray()) as unknown as SectionRow[],
    [] as SectionRow[],
  )
  const fields = useLive(
    async () => (await localDb().synced('field_definitions').toArray()) as unknown as FieldDefinitionRow[],
    [] as FieldDefinitionRow[],
  )
  const catalogue = computed(() => buildCatalogue(sections.value.value, fields.value.value))
  const loaded = computed(() => sections.loaded.value && fields.loaded.value)
  return { catalogue, loaded }
}

// --- project templates (docs/01 §10.2, data model §7.7) ----------------------------------------------------------

export interface TemplateParts {
  template: ProjectTemplateRow
  /** The allowed statuses; empty: every project status. */
  statusIds: string[]
  /** In order. */
  sections: TemplateSectionRow[]
  defaults: TemplateFieldDefaultRow[]
  /** In order (fractional `sortOrder`). */
  milestones: TemplateMilestoneRow[]
}

export interface TemplateSet {
  /** Live templates (archived ones too), in order. */
  templates: ProjectTemplateRow[]
  byId: ReadonlyMap<string, TemplateParts>
}

export function buildTemplates(rows: {
  templates: readonly ProjectTemplateRow[]
  statuses: readonly TemplateStatusRow[]
  sections: readonly TemplateSectionRow[]
  defaults: readonly TemplateFieldDefaultRow[]
  milestones: readonly TemplateMilestoneRow[]
}): TemplateSet {
  const templates = live(rows.templates).sort(
    (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name) || (a.id < b.id ? -1 : 1),
  )
  const byId = new Map<string, TemplateParts>(
    templates.map((t) => [t.id, { template: t, statusIds: [], sections: [], defaults: [], milestones: [] }]),
  )
  for (const s of live(rows.statuses)) byId.get(s.templateId)?.statusIds.push(s.statusId)
  for (const s of live(rows.sections)) byId.get(s.templateId)?.sections.push(s)
  for (const d of live(rows.defaults)) byId.get(d.templateId)?.defaults.push(d)
  for (const m of live(rows.milestones)) byId.get(m.templateId)?.milestones.push(m)
  for (const parts of byId.values()) {
    parts.sections.sort(byOrder)
    parts.milestones.sort((a, b) => compareKeyThenId(a.sortOrder, a.id, b.sortOrder, b.id))
  }
  return { templates, byId }
}

export const emptyTemplates: TemplateSet = buildTemplates({
  templates: [],
  statuses: [],
  sections: [],
  defaults: [],
  milestones: [],
})

export function loadTemplates(db: PgpmDb): Promise<TemplateSet> {
  return inTransaction(readTemplates(db))
}

async function readTemplates(db: PgpmDb): Promise<TemplateSet> {
  // Dexie promises (not async helpers), so that a repository transaction keeps its zone.
  const all = <T>(table: string) => db.synced(table).toArray() as unknown as Promise<T[]>
  return buildTemplates({
    templates: await all<ProjectTemplateRow>('project_templates'),
    statuses: await all<TemplateStatusRow>('template_statuses'),
    sections: await all<TemplateSectionRow>('template_sections'),
    defaults: await all<TemplateFieldDefaultRow>('template_field_defaults'),
    milestones: await all<TemplateMilestoneRow>('template_milestones'),
  })
}

/** The templates with their parts, live. */
export function useTemplates() {
  const set = useLive(() => readTemplates(localDb()), emptyTemplates)
  return { templates: set.value, loaded: set.loaded }
}
