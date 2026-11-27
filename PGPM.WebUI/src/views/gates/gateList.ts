import {
  decisionLabels,
  dodProgress,
  isDecided,
  isMilestoneOverdue,
  statusLabels,
  type DodProgress,
} from '@/domain/milestones'
import { today } from '@/local/items'
import {
  itemKey,
  type AssignmentRow,
  type DodItemRow,
  type GateDecisionValue,
  type ItemRow,
  type ItemShareRow,
  type MilestoneRow,
  type PersonRow,
} from '@/local/rows'

/**
 * The *Gate decisions* list's rules (docs/01 §11, docs/04 §4.2 F4), pure: every gate of the projects on this device –
 * the stop / go / scale record. The filters are the saved views' vocabulary for gate views (docs/03 §17 `viewFilters`
 * with ids: `decisions`, `templateIds`, `groupIds`, `assignedToMe`, `overdue`), the columns the ids of `COLUMNS.gate`
 * in tools/check-packages.py.
 */

export interface GateFilters {
  decisions?: GateDecisionValue[]
  /** The project's template. */
  templateIds?: string[]
  /** The project is shared with any of these groups. */
  groupIds?: string[]
  /** The signed-in user's person is responsible for the gate (a milestone assignment). */
  assignedToMe?: boolean
  /** The due date has passed and the gate isn't done or cancelled. */
  overdue?: boolean
}

export interface GateContext {
  projects: ReadonlyMap<string, ItemRow>
  /** Live DoD criteria by milestone id. */
  dod: ReadonlyMap<string, readonly DodItemRow[]>
  /** Live assignments by milestone id. */
  assignments: ReadonlyMap<string, readonly AssignmentRow[]>
  /** Live shares by item id. */
  shares: ReadonlyMap<string, readonly ItemShareRow[]>
  persons: ReadonlyMap<string, PersonRow>
  userName: (userId: string | null | undefined) => string
  me: { personId: string | null }
  /** Today as an ISO date. */
  today: string
}

export function emptyGateContext(partial: Partial<GateContext> = {}): GateContext {
  return {
    projects: new Map(),
    dod: new Map(),
    assignments: new Map(),
    shares: new Map(),
    persons: new Map(),
    userName: () => '',
    me: { personId: null },
    today: today(),
    ...partial,
  }
}

const decisionValues: readonly GateDecisionValue[] = ['pending', 'go', 'no_go', 'iterate', 'pivot', 'park']
const has = <T>(list: readonly T[] | undefined): list is readonly T[] => !!list && list.length > 0

/** The gates the list shows: live gates whose project is on the device (readable) and live. */
export function isListedGate(m: MilestoneRow, ctx: GateContext): boolean {
  const project = ctx.projects.get(m.itemId)
  return !m.deleted && m.kind === 'gate' && !!project && !project.deleted && project.type === 'project'
}

export function matchGate(m: MilestoneRow, f: GateFilters, ctx: GateContext): boolean {
  if (!isListedGate(m, ctx)) return false
  const project = ctx.projects.get(m.itemId)!
  if (has(f.decisions) && !f.decisions.includes(m.decision ?? 'pending')) return false
  if (has(f.templateIds) && !f.templateIds.includes(project.projectTemplateId ?? '')) return false
  if (has(f.groupIds) && !(ctx.shares.get(project.id) ?? []).some((s) => f.groupIds!.includes(s.groupId))) return false
  if (
    f.assignedToMe &&
    (!ctx.me.personId || !(ctx.assignments.get(m.id) ?? []).some((a) => a.personId === ctx.me.personId))
  )
    return false
  if (f.overdue && !isMilestoneOverdue(m, ctx.today)) return false
  return true
}

/** The filters without empty members. */
export function cleanGateFilters(f: GateFilters): GateFilters {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(f)) {
    if (value === undefined || value === null || value === false) continue
    if (Array.isArray(value) && !value.length) continue
    out[key] = value
  }
  return out as GateFilters
}

/** A stored filter object (a view's, the remembered one): unknown members dropped, values checked. */
export function readGateFilters(value: Record<string, unknown> | null | undefined): GateFilters {
  const v = value ?? {}
  const strings = (x: unknown) => (Array.isArray(x) ? x.filter((s): s is string => typeof s === 'string') : undefined)
  return cleanGateFilters({
    decisions: strings(v.decisions)?.filter((d): d is GateDecisionValue =>
      decisionValues.includes(d as GateDecisionValue),
    ),
    templateIds: strings(v.templateIds),
    groupIds: strings(v.groupIds),
    assignedToMe: v.assignedToMe === true,
    overdue: v.overdue === true,
  })
}

export function countGateFilters(f: GateFilters, without: readonly (keyof GateFilters)[] = []): number {
  return Object.keys(cleanGateFilters(f)).filter((k) => !without.includes(k as keyof GateFilters)).length
}

/** What a gate is found by: its title, its project's key and title, the decision note. */
export function gateText(m: MilestoneRow, ctx: GateContext): string {
  const project = ctx.projects.get(m.itemId)
  return [m.title, project ? itemKey(project) : '', project?.title, m.decisionNote].filter(Boolean).join(' ')
}

// --- columns -------------------------------------------------------------------------------------------------------

export interface GateColumn {
  id: string
  label: string
  width?: string
  required?: boolean
  align?: 'right'
}

export const gateColumns: readonly GateColumn[] = [
  { id: 'project', label: 'Project', width: '16rem', required: true },
  { id: 'title', label: 'Gate', required: true },
  { id: 'status', label: 'Status', width: '8rem' },
  { id: 'due_date', label: 'Due', width: '8rem' },
  { id: 'decision', label: 'Decision', width: '8rem' },
  { id: 'decided_at', label: 'Decided', width: '8rem' },
  { id: 'decided_by', label: 'Decided by', width: '10rem' },
  { id: 'dod_progress', label: 'DoD', width: '6rem', align: 'right' },
  { id: 'persons', label: 'Deciders', width: '12rem' },
]

export const defaultGateColumns: readonly string[] = [
  'project',
  'title',
  'decision',
  'decided_at',
  'decided_by',
  'due_date',
  'dod_progress',
]

/** The columns to show: known ones in their order, project and title always (first). */
export function visibleGateColumns(ids: readonly string[] | null | undefined): GateColumn[] {
  const all = new Map(gateColumns.map((c) => [c.id, c]))
  const chosen = (ids?.length ? ids : defaultGateColumns).filter((id, i, list) => all.has(id) && list.indexOf(id) === i)
  for (const required of ['title', 'project']) if (!chosen.includes(required)) chosen.unshift(required)
  return chosen.map((id) => all.get(id)!)
}

export function gateProgress(m: MilestoneRow, ctx: GateContext): DodProgress {
  return dodProgress(ctx.dod.get(m.id) ?? [])
}

export function deciderNames(m: MilestoneRow, ctx: GateContext): string[] {
  return (ctx.assignments.get(m.id) ?? []).map((a) => ctx.persons.get(a.personId)?.displayName ?? 'Unknown person')
}

/** The value a column sorts by; undefined for an unknown column. */
export function gateSortValue(m: MilestoneRow, id: string, ctx: GateContext): unknown {
  const project = ctx.projects.get(m.itemId)
  switch (id) {
    case 'project':
      return project ? `${project.title} ${itemKey(project)}` : null
    case 'title':
      return m.title
    case 'status':
      return statusLabels[m.status] ?? m.status
    case 'due_date':
      return m.dueDate
    case 'decision':
      return isDecided(m.decision) ? decisionValues.indexOf(m.decision) : null
    case 'decided_at':
      // A decision not synced yet has no server time: its local change's.
      return isDecided(m.decision) ? (m.decidedAt ?? m.updatedAt) : null
    case 'decided_by':
      return isDecided(m.decision) ? ctx.userName(m.decidedBy) || null : null
    case 'dod_progress': {
      const p = gateProgress(m, ctx)
      return p.total ? p.ratio : null
    }
    case 'persons':
      return deciderNames(m, ctx).join(', ') || null
    default:
      return undefined
  }
}

export function decisionText(decision: GateDecisionValue | null | undefined): string {
  return decisionLabels[decision ?? 'pending'] ?? String(decision)
}
