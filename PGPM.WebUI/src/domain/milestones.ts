import type {
  DodItemRow,
  DodState,
  GateDecisionValue,
  MilestoneKind,
  MilestoneRow,
  MilestoneStatus,
  StatusRow,
} from '@/local/rows'

/**
 * The rules of milestones and gates (docs/01 §11, docs/04 F4/F7), pure: labels, the Definition of Done's progress, when
 * a milestone may be marked done (and when a gate counts as done), *overdue*, and the project status a gate decision
 * suggests (the template's `on_go` / `on_no_go`, docs/02 §7.7).
 */

export const kindLabels: Record<MilestoneKind, string> = { milestone: 'Milestone', gate: 'Gate' }

export const statusOptions: readonly { value: MilestoneStatus; label: string }[] = [
  { value: 'planned', label: 'Planned' },
  { value: 'in_progress', label: 'In progress' },
  { value: 'done', label: 'Done' },
  { value: 'cancelled', label: 'Cancelled' },
]

export const statusLabels: Record<MilestoneStatus, string> = Object.fromEntries(
  statusOptions.map((o) => [o.value, o.label]),
) as Record<MilestoneStatus, string>

export const decisionOptions: readonly {
  value: GateDecisionValue
  label: string
  icon: string
  severity: 'secondary' | 'success' | 'danger' | 'warn' | 'info' | 'contrast'
}[] = [
  { value: 'pending', label: 'Pending', icon: 'pi pi-hourglass', severity: 'secondary' },
  { value: 'go', label: 'Go', icon: 'pi pi-check-circle', severity: 'success' },
  { value: 'no_go', label: 'No-go', icon: 'pi pi-ban', severity: 'danger' },
  { value: 'iterate', label: 'Iterate', icon: 'pi pi-replay', severity: 'warn' },
  { value: 'pivot', label: 'Pivot', icon: 'pi pi-directions-alt', severity: 'info' },
  { value: 'park', label: 'Park', icon: 'pi pi-pause-circle', severity: 'contrast' },
]

export const decisionLabels: Record<GateDecisionValue, string> = Object.fromEntries(
  decisionOptions.map((o) => [o.value, o.label]),
) as Record<GateDecisionValue, string>

export const dodStateLabels: Record<DodState, string> = { open: 'Open', done: 'Done', na: 'n/a' }

/** A decision was recorded (not none, not pending). */
export function isDecided(decision: GateDecisionValue | null | undefined): decision is GateDecisionValue {
  return !!decision && decision !== 'pending'
}

export interface DodProgress {
  done: number
  na: number
  open: number
  total: number
  /** The criteria that count: all but the n/a ones. */
  counted: number
  /** done ÷ (all − n/a), 0–1; 1 when nothing counts (no criteria, or all n/a). */
  ratio: number
  /** Every counted criterion done (true without criteria). */
  complete: boolean
}

/** The DoD's progress (docs/01 §11): done ÷ (all − n/a). No criteria, or only n/a ones: complete (100 %). */
export function dodProgress(items: readonly Pick<DodItemRow, 'state' | 'deleted'>[]): DodProgress {
  const live = items.filter((i) => !i.deleted)
  const done = live.filter((i) => i.state === 'done').length
  const na = live.filter((i) => i.state === 'na').length
  const total = live.length
  const counted = total - na
  return {
    done,
    na,
    open: total - done - na,
    total,
    counted,
    ratio: counted > 0 ? done / counted : 1,
    complete: done === counted,
  }
}

/** "3 / 5" (the n/a ones left out), or "–" without criteria. */
export function progressText(progress: DodProgress): string {
  return progress.total === 0 ? '–' : `${progress.done} / ${progress.counted}`
}

export function dodComplete(items: readonly Pick<DodItemRow, 'state' | 'deleted'>[]): boolean {
  return dodProgress(items).complete
}

/** Why a criterion's state can't be saved: *n/a* needs a note (the evidence), like the server (docs/02 §8). */
export function dodStateError(state: DodState, note: string | null | undefined): string | null {
  return state === 'na' && !note?.trim() ? 'n/a needs a note (why it doesn’t apply).' : null
}

/**
 * What marking a milestone *done* needs (docs/01 §11): nothing when its DoD is complete; else the user confirms an
 * override (the status change is in the History). A gate also needs a decision other than *pending* to count as done –
 * marking it done without one is an override as well.
 */
export function doneBlockers(
  milestone: Pick<MilestoneRow, 'kind' | 'decision'>,
  items: readonly Pick<DodItemRow, 'state' | 'deleted'>[],
): string[] {
  const blockers: string[] = []
  const progress = dodProgress(items)
  if (!progress.complete) {
    blockers.push(
      progress.open === 1
        ? '1 criterion of the Definition of Done is open'
        : `${progress.open} criteria of the Definition of Done are open`,
    )
  }
  if (milestone.kind === 'gate' && !isDecided(milestone.decision)) blockers.push('no gate decision is recorded')
  return blockers
}

/** The confirmation text of a *done* override, or null when the milestone may simply be marked done. */
export function overrideMessage(
  milestone: Pick<MilestoneRow, 'kind' | 'decision'>,
  items: readonly Pick<DodItemRow, 'state' | 'deleted'>[],
): string | null {
  const blockers = doneBlockers(milestone, items)
  if (!blockers.length) return null
  const what = blockers.join(' and ')
  return `${what.charAt(0).toUpperCase()}${what.slice(1)}. Mark it done anyway? The change is recorded in the History.`
}

/** Whether a milestone counts as done: status done; a gate also with its DoD complete and a decision recorded. */
export function countsAsDone(
  milestone: Pick<MilestoneRow, 'kind' | 'status' | 'decision'>,
  items: readonly Pick<DodItemRow, 'state' | 'deleted'>[],
): boolean {
  if (milestone.status !== 'done') return false
  return milestone.kind !== 'gate' || (dodComplete(items) && isDecided(milestone.decision))
}

/** Its due date has passed while it isn't done or cancelled (`today` as ISO date). */
export function isMilestoneOverdue(milestone: Pick<MilestoneRow, 'dueDate' | 'status'>, today: string): boolean {
  return (
    !!milestone.dueDate && milestone.dueDate < today && milestone.status !== 'done' && milestone.status !== 'cancelled'
  )
}

/** An ISO date plus days (`2026-11-27` + 56 → `2027-01-22`), in UTC so no zone shifts it. */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number)
  const next = new Date(Date.UTC(y!, m! - 1, d! + days))
  return next.toISOString().slice(0, 10)
}

/**
 * The project status a gate decision suggests (docs/01 §11): *go* → `onGoStatusId`, *no-go* → `onNoGoStatusId`, none
 * for the others; only a known, not archived status of projects that the project's template allows (`allowedIds`
 * empty: every one) and that isn't the project's status already.
 */
export function suggestedStatus(
  milestone: Pick<MilestoneRow, 'kind' | 'onGoStatusId' | 'onNoGoStatusId'>,
  decision: GateDecisionValue | null | undefined,
  project: { statusId: string | null },
  statuses: ReadonlyMap<string, StatusRow>,
  allowedIds: readonly string[],
): StatusRow | null {
  if (milestone.kind !== 'gate') return null
  const id = decision === 'go' ? milestone.onGoStatusId : decision === 'no_go' ? milestone.onNoGoStatusId : null
  if (!id || id === project.statusId) return null
  const status = statuses.get(id)
  if (!status || status.deleted || status.isArchived || status.itemType !== 'project') return null
  if (allowedIds.length && !allowedIds.includes(id)) return null
  return status
}

/** The order of milestones (fractional `sortOrder`, ties by id). */
export function compareMilestones(
  a: Pick<MilestoneRow, 'sortOrder' | 'id'>,
  b: Pick<MilestoneRow, 'sortOrder' | 'id'>,
) {
  return a.sortOrder < b.sortOrder ? -1 : a.sortOrder > b.sortOrder ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0
}
