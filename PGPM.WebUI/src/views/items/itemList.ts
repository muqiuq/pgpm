import { normalise } from '@/composables/useLocalTable'
import { isEmptyValue } from '@/domain/fields/fieldWidget'
import { searchText, sortValueOf, timeboxReason as timeboxOf } from '@/domain/fields/renderValue'
import { evaluateScore } from '@/domain/score'
import { emptyCatalogue, type Catalogue } from '@/local/catalogue'
import { isOverdue, overdueReason, daysInStatus, relations, today } from '@/local/items'
import {
  itemKey,
  type FieldDefinitionRow,
  type AssignmentRow,
  type ItemLinkRow,
  type ItemRow,
  type ItemShareRow,
  type ItemType,
  type LinkRelation,
  type PersonRow,
  type StatusCategory,
  type StatusRow,
} from '@/local/rows'
import type { Quadrant } from '@/maps/matrixLayout'

/**
 * The item lists' rules (docs/01 §4.3), pure so that they are tested without a page: the filters – the saved views'
 * vocabulary of docs/03 §17 (`spec/config-package.schema.json` `viewFilters`, with ids instead of keys, field filters by
 * `field:<key>`) plus the list's own –, the columns per type (the ids of `COLUMNS` in tools/check-packages.py and a
 * `field:<key>` column for every field of the sections that apply to the type, scores included), and what each column
 * sorts by. Further columns (phase 5's matrix *quadrant*) come in through {@link registerColumns}.
 */

/** A dynamic field filter (`viewFilters.fields`): `field` is `field:<key>`. */
export interface FieldFilter {
  field: string
  op: 'eq' | 'in' | 'contains' | 'range' | 'set' | 'unset'
  value?: string | number | boolean
  values?: string[]
  min?: number | string
  max?: number | string
}

export interface HasLinkFilter {
  relation: LinkRelation
  /** `outgoing` (default): the item is the link's source. */
  direction?: 'outgoing' | 'incoming' | 'any'
  /** The other end's types; a target not on this device (restricted) counts. */
  targetTypes?: ItemType[]
}

export interface ItemFilters {
  // --- the saved views' filters (docs/03 §17) ---
  statusIds?: string[]
  categories?: StatusCategory[]
  templateIds?: string[]
  /** Shared with any of these groups. */
  groupIds?: string[]
  assignedToMe?: boolean
  /** Status aging (max days), a lead's next action due, a passed timebox date field – never in a closed status. */
  overdue?: boolean
  hasLink?: HasLinkFilter
  /** Dynamic field filters (ideas and projects): all must match. */
  fields?: FieldFilter[]
  // --- the list's own ---
  /** Owners (user ids). */
  ownerIds?: string[]
  /** Assigned persons (person ids). */
  personIds?: string[]
  colors?: string[]
  /** ISO dates (`2026-11-27`), inclusive, in the browser's zone. */
  createdFrom?: string
  createdTo?: string
  modifiedFrom?: string
  modifiedTo?: string
  /** Notes: pinned ones only. */
  pinned?: boolean
  /** Owned by me. */
  mine?: boolean
  /** Not in a closed status. */
  hideClosed?: boolean
  /** In these quadrants of a matrix map (docs/01 §12.8; views/maps/quadrants.ts). */
  quadrant?: { mapId: string; quadrants: Quadrant[] }
}

/** What the filters and columns look up besides the item. */
export interface ListContext {
  statuses: ReadonlyMap<string, StatusRow>
  /** Live assignments by item id. */
  assignments: ReadonlyMap<string, readonly AssignmentRow[]>
  /** Live shares by item id. */
  shares: ReadonlyMap<string, readonly ItemShareRow[]>
  /** Live links by source item id. */
  outgoing: ReadonlyMap<string, readonly ItemLinkRow[]>
  /** Live links by target item id. */
  incoming: ReadonlyMap<string, readonly ItemLinkRow[]>
  /** The types of the items on this device, by id (a link's other end). */
  types: ReadonlyMap<string, ItemType>
  persons: ReadonlyMap<string, PersonRow>
  /** A user's name (owner). */
  userName: (userId: string | null | undefined) => string
  /** Project template names by id. */
  templates: ReadonlyMap<string, string>
  me: { userId: string | null; personId: string | null }
  now: number
  /** The sections and fields the user sees (dynamic columns, filters, scores). */
  catalogue: Catalogue
  /** The items' field values by key (`field_values`), by item id. */
  fieldValues: ReadonlyMap<string, Readonly<Record<string, unknown>>>
  /** Scores computed for this context, by item id then field key (filled as they are asked for). */
  scoreCache: Map<string, Map<string, number | null>>
  /** Columns beyond the type's own (a matrix map's quadrant): the value shown and sorted by. */
  columnValue?: (item: ItemRow, id: string) => unknown
  /** The item's quadrant on a matrix map; null: not on it, or not assessed. */
  quadrantOf?: (item: ItemRow, mapId: string) => Quadrant | null
}

/** An empty context (tests, a list before its lookups arrive). */
export function emptyContext(partial: Partial<ListContext> = {}): ListContext {
  return {
    statuses: new Map(),
    assignments: new Map(),
    shares: new Map(),
    outgoing: new Map(),
    incoming: new Map(),
    types: new Map(),
    persons: new Map(),
    userName: () => '',
    templates: new Map(),
    me: { userId: null, personId: null },
    now: Date.now(),
    catalogue: emptyCatalogue,
    fieldValues: new Map(),
    scoreCache: new Map(),
    ...partial,
  }
}

// --- dynamic fields ----------------------------------------------------------------------------------------------

const FIELD = 'field:'

/** The field a column id or filter names (`field:<key>`), if the user sees it. */
export function fieldOfColumn(id: string, ctx: Pick<ListContext, 'catalogue'>): FieldDefinitionRow | undefined {
  return id.startsWith(FIELD) ? ctx.catalogue.fieldByKey.get(id.slice(FIELD.length)) : undefined
}

/** A field's value on an item: the stored one, or a score's result (computed once per context). */
export function itemFieldValue(item: Pick<ItemRow, 'id'>, field: FieldDefinitionRow, ctx: ListContext): unknown {
  const values = ctx.fieldValues.get(item.id) ?? {}
  if (field.type !== 'score') return values[field.key]
  let cache = ctx.scoreCache.get(item.id)
  if (!cache) ctx.scoreCache.set(item.id, (cache = new Map()))
  if (!cache.has(field.key)) {
    const def = ctx.catalogue.defsByKey[field.key] ?? field
    cache.set(field.key, evaluateScore(def, ctx.catalogue.defsByKey, values))
  }
  return cache.get(field.key) ?? null
}

/** Why an item's timebox date fields make it overdue (docs/01 §6.1), or null; never in a closed status. */
export function timeboxReason(item: ItemRow, ctx: ListContext): string | null {
  if (item.type !== 'idea' && item.type !== 'project') return null
  const status = item.statusId ? ctx.statuses.get(item.statusId) : undefined
  if (status?.category === 'closed') return null
  const values = ctx.fieldValues.get(item.id)
  if (!values) return null
  const fields = timeboxFields(ctx.catalogue.fields)
  return fields.length ? timeboxOf(fields, values, today(ctx.now), false) : null
}

const timeboxCache = new WeakMap<readonly FieldDefinitionRow[], FieldDefinitionRow[]>()

/** The fields with `config.timebox` (cached per catalogue). */
function timeboxFields(fields: readonly FieldDefinitionRow[]): FieldDefinitionRow[] {
  let list = timeboxCache.get(fields)
  if (!list) timeboxCache.set(fields, (list = fields.filter((f) => f.config?.timebox === true)))
  return list
}

function numeric(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value)
  return null
}

/** Whether an item passes one field filter; a field the user doesn't see matches only `unset`. */
export function matchField(item: ItemRow, filter: FieldFilter, ctx: ListContext): boolean {
  const field = fieldOfColumn(filter.field, ctx)
  if (!field) return filter.op === 'unset'
  const value = itemFieldValue(item, field, ctx)
  const empty = field.type === 'score' ? value === null || value === undefined : isEmptyValue(value)
  switch (filter.op) {
    case 'set':
      return !empty
    case 'unset':
      return empty
    case 'eq':
      if (empty) return false
      if (field.type === 'bool') return (value === true) === (filter.value === true || filter.value === 'true')
      if (Array.isArray(value)) return value.includes(filter.value)
      return value === filter.value || (numeric(value) !== null && numeric(value) === numeric(filter.value))
    case 'in': {
      const wanted = filter.values ?? []
      if (empty || !wanted.length) return false
      return Array.isArray(value) ? value.some((v) => wanted.includes(String(v))) : wanted.includes(String(value))
    }
    case 'contains': {
      const needle = normalise(String(filter.value ?? '').trim())
      if (!needle) return true
      if (empty) return false
      const ctxNames = { personName: (id: string) => ctx.persons.get(id)?.displayName }
      return normalise(searchText(field, value, ctxNames) || String(value)).includes(needle)
    }
    case 'range': {
      if (empty) return false
      if (field.type === 'date' || field.type === 'datetime') {
        const text = String(value)
        return (
          (filter.min === undefined || text >= String(filter.min)) &&
          (filter.max === undefined || text <= String(filter.max))
        )
      }
      const n = numeric(value)
      if (n === null) return false
      const min = numeric(filter.min)
      const max = numeric(filter.max)
      return (min === null || n >= min) && (max === null || n <= max)
    }
    default:
      return true
  }
}

/** A stored field filter list, checked: unknown members and ops dropped. */
export function readFieldFilters(value: unknown): FieldFilter[] | undefined {
  if (!Array.isArray(value)) return undefined
  const ops = ['eq', 'in', 'contains', 'range', 'set', 'unset']
  const out: FieldFilter[] = []
  for (const raw of value) {
    if (typeof raw !== 'object' || raw === null) continue
    const r = raw as Record<string, unknown>
    if (typeof r.field !== 'string' || !r.field.startsWith(FIELD) || typeof r.op !== 'string' || !ops.includes(r.op))
      continue
    const f: FieldFilter = { field: r.field, op: r.op as FieldFilter['op'] }
    if (['string', 'number', 'boolean'].includes(typeof r.value)) f.value = r.value as string | number | boolean
    if (Array.isArray(r.values)) f.values = r.values.filter((v): v is string => typeof v === 'string')
    if (typeof r.min === 'number' || typeof r.min === 'string') f.min = r.min
    if (typeof r.max === 'number' || typeof r.max === 'string') f.max = r.max
    out.push(f)
  }
  return out.length ? out : undefined
}

// --- further columns (a hook for phase 5's matrix quadrant) ---------------------------------------------------------

/** Columns another part of the app adds to the item lists (phase 5: *quadrant* from the matrix maps). */
export interface ColumnProvider {
  /** The columns it adds for a type (ids must not collide with the built-in ones or `field:`). */
  columns(type: ItemType, ctx: ListContext): ColumnDef[]
  /** A column's value for an item (sorted by, shown as text); undefined: not its column. */
  value(item: ItemRow, id: string, ctx: ListContext): unknown
}

const providers: ColumnProvider[] = []

/** Adds columns to the item lists; answers a function that removes them again. */
export function registerColumns(provider: ColumnProvider): () => void {
  providers.push(provider)
  return () => {
    const at = providers.indexOf(provider)
    if (at >= 0) providers.splice(at, 1)
  }
}

const has = <T>(list: readonly T[] | undefined): list is readonly T[] => !!list && list.length > 0

/** The local calendar day of a timestamp. */
function dayOf(timestamp: string): string {
  const time = Date.parse(timestamp)
  return Number.isNaN(time) ? '' : today(time)
}

function linkMatches(item: ItemRow, filter: HasLinkFilter, ctx: ListContext): boolean {
  const direction = filter.direction ?? 'outgoing'
  const typeFits = (id: string) => {
    if (!has(filter.targetTypes)) return true
    const type = ctx.types.get(id)
    return type === undefined || filter.targetTypes.includes(type)
  }
  const out = (ctx.outgoing.get(item.id) ?? []).some((l) => l.relation === filter.relation && typeFits(l.toItemId))
  if (direction === 'outgoing') return out
  const inc = (ctx.incoming.get(item.id) ?? []).some((l) => l.relation === filter.relation && typeFits(l.fromItemId))
  return direction === 'incoming' ? inc : out || inc
}

/** Whether an item passes the filters (all given ones; values inside one filter are alternatives). */
export function matchItem(item: ItemRow, f: ItemFilters, ctx: ListContext): boolean {
  if (item.deleted) return false
  const status = item.statusId ? ctx.statuses.get(item.statusId) : undefined
  if (has(f.statusIds) && !f.statusIds.includes(item.statusId ?? '')) return false
  if (has(f.categories) && (!status || !f.categories.includes(status.category))) return false
  if (f.hideClosed && status?.category === 'closed') return false
  if (has(f.templateIds) && !f.templateIds.includes(item.projectTemplateId ?? '')) return false
  if (has(f.groupIds) && !(ctx.shares.get(item.id) ?? []).some((s) => f.groupIds!.includes(s.groupId))) return false
  const assigned = ctx.assignments.get(item.id) ?? []
  if (f.assignedToMe && (!ctx.me.personId || !assigned.some((a) => a.personId === ctx.me.personId))) return false
  if (has(f.personIds) && !assigned.some((a) => f.personIds!.includes(a.personId))) return false
  if (f.overdue && !isOverdue(item, status, ctx.now) && !timeboxReason(item, ctx)) return false
  if (f.hasLink?.relation && !linkMatches(item, f.hasLink, ctx)) return false
  if (has(f.fields) && !f.fields.every((filter) => matchField(item, filter, ctx))) return false
  if (has(f.ownerIds) && !f.ownerIds.includes(item.ownerUserId)) return false
  if (f.mine && item.ownerUserId !== ctx.me.userId) return false
  if (has(f.colors) && !f.colors.includes((item.color ?? '').toLowerCase())) return false
  if (f.pinned && !item.pinned) return false
  const created = dayOf(item.createdAt)
  if (f.createdFrom && created < f.createdFrom) return false
  if (f.createdTo && created > f.createdTo) return false
  const modified = dayOf(item.updatedAt)
  if (f.modifiedFrom && modified < f.modifiedFrom) return false
  if (f.modifiedTo && modified > f.modifiedTo) return false
  if (f.quadrant?.mapId && has(f.quadrant.quadrants)) {
    const q = ctx.quadrantOf?.(item, f.quadrant.mapId)
    if (!q || !f.quadrant.quadrants.includes(q)) return false
  }
  return true
}

/** The filters without empty members (what is remembered and compared). */
export function cleanFilters(f: ItemFilters): ItemFilters {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(f)) {
    if (value === undefined || value === null || value === false || value === '') continue
    if (Array.isArray(value) && !value.length) continue
    if (key === 'hasLink' && !(value as HasLinkFilter).relation) continue
    if (key === 'quadrant' && !(value as ItemFilters['quadrant'])?.quadrants?.length) continue
    out[key] = value
  }
  return out as ItemFilters
}

/** A stored filter object (a view's, the remembered one) as filters: unknown members dropped, types checked. */
export function readFilters(value: Record<string, unknown> | null | undefined): ItemFilters {
  const v = value ?? {}
  const strings = (x: unknown) => (Array.isArray(x) ? x.filter((s): s is string => typeof s === 'string') : undefined)
  const date = (x: unknown) => (typeof x === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(x) ? x : undefined)
  const link = v.hasLink as Partial<HasLinkFilter> | undefined
  const quadrant = v.quadrant as { mapId?: unknown; quadrants?: unknown } | undefined
  return cleanFilters({
    statusIds: strings(v.statusIds),
    categories: strings(v.categories)?.filter((c): c is StatusCategory => ['open', 'active', 'closed'].includes(c)),
    templateIds: strings(v.templateIds),
    groupIds: strings(v.groupIds),
    assignedToMe: v.assignedToMe === true,
    overdue: v.overdue === true,
    hasLink:
      link && typeof link.relation === 'string'
        ? {
            relation: link.relation,
            direction: link.direction ?? 'outgoing',
            ...(strings(link.targetTypes)?.length ? { targetTypes: strings(link.targetTypes) as ItemType[] } : {}),
          }
        : undefined,
    fields: readFieldFilters(v.fields),
    ownerIds: strings(v.ownerIds),
    personIds: strings(v.personIds),
    colors: strings(v.colors),
    createdFrom: date(v.createdFrom),
    createdTo: date(v.createdTo),
    modifiedFrom: date(v.modifiedFrom),
    modifiedTo: date(v.modifiedTo),
    pinned: v.pinned === true,
    mine: v.mine === true,
    hideClosed: v.hideClosed === true,
    quadrant:
      quadrant && typeof quadrant.mapId === 'string'
        ? {
            mapId: quadrant.mapId,
            quadrants: (strings(quadrant.quadrants) ?? []).filter((q): q is Quadrant =>
              ['tl', 'tr', 'bl', 'br'].includes(q),
            ),
          }
        : undefined,
  })
}

/** How many filters are set (the *Filters (n)* badge), the quick ones excluded if asked. */
export function countFilters(f: ItemFilters, without: readonly (keyof ItemFilters)[] = []): number {
  return Object.keys(cleanFilters(f)).filter((k) => !without.includes(k as keyof ItemFilters)).length
}

/** The text an item is found by: key, title, description, the lead's text fields and – with a context – field texts. */
export function itemText(item: ItemRow, ctx?: ListContext): string {
  const fixed = [
    itemKey(item),
    item.title,
    item.description,
    item.leadOrganisation,
    item.leadNextAction,
    item.leadSource,
  ]
  const values = ctx?.fieldValues.get(item.id)
  if (values && ctx) {
    const names = { personName: (id: string) => ctx.persons.get(id)?.displayName }
    for (const [key, value] of Object.entries(values)) {
      const field = ctx.catalogue.fieldByKey.get(key)
      if (field) fixed.push(searchText(field, value, names))
    }
  }
  return fixed.filter(Boolean).join(' ')
}

// --- columns -----------------------------------------------------------------------------------------------------

export interface ColumnDef {
  id: string
  label: string
  /** CSS width; none: shares the rest. */
  width?: string
  /** Can't be hidden. */
  required?: boolean
  align?: 'right'
  /** A `field:<key>` column: its field. */
  field?: FieldDefinitionRow
}

const column = (id: string, label: string, width?: string, extra: Partial<ColumnDef> = {}): ColumnDef => ({
  id,
  label,
  width,
  ...extra,
})

const common = {
  key: column('key', 'Key', '6.5rem', { required: true }),
  title: column('title', 'Title', undefined, { required: true }),
  status: column('status', 'Status', '11rem'),
  owner: column('owner', 'Owner', '10rem'),
  persons: column('persons', 'Persons', '12rem'),
  color: column('color', 'Colour', '5rem'),
  created_at: column('created_at', 'Created', '8rem'),
  updated_at: column('updated_at', 'Modified', '8rem'),
}

const byType: Record<ItemType, ColumnDef[]> = {
  note: [
    common.key,
    common.title,
    column('pinned', 'Pinned', '5.5rem'),
    common.owner,
    common.persons,
    column('links', 'Links', '9rem'),
    common.color,
    common.created_at,
    common.updated_at,
  ],
  idea: [
    common.key,
    common.title,
    common.status,
    column('days_in_status', 'Days in status', '7rem', { align: 'right' }),
    common.owner,
    common.persons,
    column('links', 'Links', '9rem'),
    common.color,
    common.created_at,
    common.updated_at,
  ],
  lead: [
    common.key,
    common.title,
    common.status,
    column('days_in_status', 'Days in status', '7rem', { align: 'right' }),
    column('organisation', 'Organisation', '11rem'),
    column('value', 'Value', '8rem', { align: 'right' }),
    column('probability', 'Probability', '6.5rem', { align: 'right' }),
    column('decision_date', 'Decision', '8rem'),
    column('next_action', 'Next action', '12rem'),
    column('next_action_due', 'Due', '7.5rem'),
    column('source', 'Source', '9rem'),
    common.owner,
    common.persons,
    common.color,
    common.created_at,
    common.updated_at,
  ],
  project: [
    common.key,
    common.title,
    common.status,
    column('days_in_status', 'Days in status', '7rem', { align: 'right' }),
    column('template', 'Template', '10rem'),
    column('start_date', 'Start', '7.5rem'),
    column('end_date', 'End', '7.5rem'),
    common.owner,
    common.persons,
    column('links', 'Links', '9rem'),
    common.color,
    common.created_at,
    common.updated_at,
  ],
}

const defaults: Record<ItemType, string[]> = {
  note: ['key', 'title', 'owner', 'updated_at'],
  idea: ['key', 'title', 'status', 'persons', 'owner', 'updated_at'],
  lead: ['key', 'title', 'status', 'organisation', 'value', 'probability', 'next_action_due', 'owner'],
  project: ['key', 'title', 'status', 'persons', 'start_date', 'end_date', 'owner'],
}

const fieldWidths: Partial<Record<FieldDefinitionRow['type'], string>> = {
  bool: '6rem',
  number: '8rem',
  rating: '6.5rem',
  score: '7.5rem',
  date: '8rem',
  datetime: '10rem',
  select: '10rem',
}

/** The `field:<key>` columns of a type: every field (not archived) of the sections that apply to it, in order. */
export function fieldColumns(type: ItemType, catalogue: Catalogue): ColumnDef[] {
  if (type !== 'idea' && type !== 'project') return []
  const sections = new Set(catalogue.sections.filter((s) => s.appliesTo.includes(type)).map((s) => s.id))
  return catalogue.fields
    .filter((f) => !f.isArchived && sections.has(f.sectionId))
    .map((f) => ({
      id: `${FIELD}${f.key}`,
      label: f.label,
      width: fieldWidths[f.type] ?? '12rem',
      align: f.type === 'number' || f.type === 'score' || f.type === 'rating' ? ('right' as const) : undefined,
      field: f,
    }))
}

/**
 * Every column a type's list can show, in the chooser's order: the built-in ones, further ones (registered providers,
 * and `extra` – a matrix map's quadrant, phase 5), the fields.
 */
export function columnsFor(type: ItemType, ctx?: ListContext, extra: readonly ColumnDef[] = []): ColumnDef[] {
  if (!ctx) return extra.length ? [...byType[type], ...extra] : byType[type]
  return [
    ...byType[type],
    ...providers.flatMap((p) => p.columns(type, ctx)),
    ...extra,
    ...fieldColumns(type, ctx.catalogue),
  ]
}

export function defaultColumns(type: ItemType): string[] {
  return [...defaults[type]]
}

/** The columns to show for the chosen ids: known ones in their order, key and title always (first). */
export function visibleColumns(
  type: ItemType,
  ids: readonly string[] | null | undefined,
  ctx?: ListContext,
  extra: readonly ColumnDef[] = [],
): ColumnDef[] {
  const all = new Map(columnsFor(type, ctx, extra).map((c) => [c.id, c]))
  const chosen = (ids?.length ? ids : defaults[type]).filter((id, i, list) => all.has(id) && list.indexOf(id) === i)
  for (const required of ['title', 'key']) if (!chosen.includes(required)) chosen.unshift(required)
  return chosen.map((id) => all.get(id)!)
}

/** The names of an item's assigned persons, in order. */
export function personNames(item: ItemRow, ctx: ListContext): string[] {
  return (ctx.assignments.get(item.id) ?? []).map((a) => ctx.persons.get(a.personId)?.displayName ?? 'Unknown person')
}

/** An item's links (both directions) as text: "2 · is learning of". */
export function linkSummary(item: ItemRow, ctx: ListContext): { count: number; text: string } {
  const out = ctx.outgoing.get(item.id) ?? []
  const inc = ctx.incoming.get(item.id) ?? []
  const labels = new Set<string>()
  for (const l of out) labels.add(relations.find((r) => r.value === l.relation)?.label ?? l.relation)
  for (const l of inc) labels.add(relations.find((r) => r.value === l.relation)?.inverse ?? l.relation)
  return { count: out.length + inc.length, text: [...labels].join(', ') }
}

/** The value a column sorts by; undefined for a column this version doesn't know. */
export function sortValue(item: ItemRow, id: string, ctx: ListContext): unknown {
  switch (id) {
    case 'key':
      return item.number ?? Number.MAX_SAFE_INTEGER
    case 'title':
      return item.title
    case 'status':
      return item.statusId ? (ctx.statuses.get(item.statusId)?.sortOrder ?? null) : null
    case 'owner':
      return ctx.userName(item.ownerUserId) || null
    case 'persons':
      return personNames(item, ctx).join(', ') || null
    case 'color':
      return item.color
    case 'created_at':
      return item.createdAt
    case 'updated_at':
      return item.updatedAt
    case 'pinned':
      return !!item.pinned
    case 'links':
      return linkSummary(item, ctx).count || null
    case 'days_in_status':
      return daysInStatus(item, ctx.now)
    case 'organisation':
      return item.leadOrganisation
    case 'value':
      return item.leadValue === null || item.leadValue === undefined ? null : Number(item.leadValue)
    case 'probability':
      return item.leadProbability
    case 'decision_date':
      return item.leadDecisionDate
    case 'next_action':
      return item.leadNextAction
    case 'next_action_due':
      return item.leadNextActionDue
    case 'source':
      return item.leadSource
    case 'template':
      return item.projectTemplateId ? (ctx.templates.get(item.projectTemplateId) ?? null) : null
    case 'start_date':
      return item.projectStartDate
    case 'end_date':
      return item.projectEndDate
    default: {
      const field = fieldOfColumn(id, ctx)
      if (field) {
        const names = { personName: (p: string) => ctx.persons.get(p)?.displayName }
        return sortValueOf(field, itemFieldValue(item, field, ctx), names)
      }
      for (const provider of providers) {
        const value = provider.value(item, id, ctx)
        if (value !== undefined) return value
      }
      return ctx.columnValue?.(item, id)
    }
  }
}

/** The item's status and why it is overdue (status aging, a lead's next action, a timebox date), for the status cell. */
export function statusOf(item: ItemRow, ctx: ListContext): { status: StatusRow | undefined; overdue: string | null } {
  const status = item.statusId ? ctx.statuses.get(item.statusId) : undefined
  return { status, overdue: overdueReason(item, status, ctx.now) ?? timeboxReason(item, ctx) }
}

/** "CHF 12,500" */
export function formatValue(value: number | null | undefined, currency: string | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return ''
  return `${currency || 'CHF'} ${Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 })}`
}

/** "12 Nov 2026" for an ISO date. */
export function formatDay(value: string | null | undefined): string {
  if (!value) return ''
  const [y, m, d] = value.split('-').map(Number)
  if (!y || !m || !d) return value
  return new Date(y, m - 1, d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

/** Groups rows by a member (the live lookups of a list). */
export function groupBy<T extends { deleted?: boolean }>(rows: readonly T[], key: (row: T) => string | null) {
  const map = new Map<string, T[]>()
  for (const row of rows) {
    if (row.deleted) continue
    const k = key(row)
    if (!k) continue
    const list = map.get(k)
    if (list) list.push(row)
    else map.set(k, [row])
  }
  return map
}
