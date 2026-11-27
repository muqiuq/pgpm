import { describe, expect, it } from 'vitest'
import type { DodState, StatusRow } from '@/local/rows'
import {
  addDays,
  countsAsDone,
  dodComplete,
  dodProgress,
  dodStateError,
  doneBlockers,
  isMilestoneOverdue,
  overrideMessage,
  progressText,
  suggestedStatus,
} from './milestones'

const dod = (...states: DodState[]) => states.map((state) => ({ state, deleted: false }))

function status(id: string, extra: Partial<StatusRow> = {}): StatusRow {
  return {
    id,
    seq: 1,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: '2026-11-01T00:00:00Z',
    createdBy: null,
    updatedAt: '2026-11-01T00:00:00Z',
    updatedBy: null,
    itemType: 'project',
    name: id,
    color: '#64748b',
    icon: null,
    category: 'active',
    sortOrder: 10,
    isDefault: false,
    maxDays: null,
    isArchived: false,
    ...extra,
  }
}

describe('the Definition of Done (docs/01 §11, docs/04 F7)', () => {
  it('counts done ÷ (all − n/a); deleted criteria don’t count', () => {
    const p = dodProgress([...dod('done', 'done', 'open', 'na', 'open'), { state: 'open', deleted: true }])
    expect(p).toMatchObject({ done: 2, na: 1, open: 2, total: 5, counted: 4, complete: false })
    expect(p.ratio).toBe(0.5)
    expect(progressText(p)).toBe('2 / 4')
  })

  it('is complete when every counted criterion is done – also with none, or only n/a ones', () => {
    expect(dodComplete(dod('done', 'na', 'done'))).toBe(true)
    expect(dodProgress([])).toMatchObject({ ratio: 1, complete: true })
    expect(progressText(dodProgress([]))).toBe('–')
    expect(dodProgress(dod('na', 'na'))).toMatchObject({ ratio: 1, complete: true, counted: 0 })
    expect(dodComplete(dod('done', 'open'))).toBe(false)
  })

  it('needs a note for n/a', () => {
    expect(dodStateError('na', '  ')).toMatch(/note/)
    expect(dodStateError('na', 'No personal data')).toBeNull()
    expect(dodStateError('done', null)).toBeNull()
  })
})

describe('done, overdue and the gate decision (docs/01 §11)', () => {
  it('marks a milestone done freely when its DoD is complete; otherwise it is an override', () => {
    const milestone = { kind: 'milestone' as const, decision: null }
    expect(doneBlockers(milestone, dod('done', 'na'))).toEqual([])
    expect(overrideMessage(milestone, dod('done', 'na'))).toBeNull()
    expect(overrideMessage(milestone, dod('open', 'done'))).toBe(
      '1 criterion of the Definition of Done is open. Mark it done anyway? The change is recorded in the History.',
    )
  })

  it('needs a decision other than pending for a gate', () => {
    expect(doneBlockers({ kind: 'gate', decision: 'pending' }, dod('done'))).toEqual(['no gate decision is recorded'])
    expect(doneBlockers({ kind: 'gate', decision: 'go' }, dod('done'))).toEqual([])
    expect(overrideMessage({ kind: 'gate', decision: null }, dod('open', 'open'))).toMatch(
      /^2 criteria of the Definition of Done are open and no gate decision is recorded\./,
    )
    expect(countsAsDone({ kind: 'gate', status: 'done', decision: 'go' }, dod('done'))).toBe(true)
    expect(countsAsDone({ kind: 'gate', status: 'done', decision: 'pending' }, dod('done'))).toBe(false)
    expect(countsAsDone({ kind: 'gate', status: 'done', decision: 'go' }, dod('open'))).toBe(false)
    expect(countsAsDone({ kind: 'milestone', status: 'done', decision: null }, dod('open'))).toBe(true)
    expect(countsAsDone({ kind: 'milestone', status: 'in_progress', decision: null }, [])).toBe(false)
  })

  it('is overdue when the due date has passed and it isn’t done or cancelled', () => {
    expect(isMilestoneOverdue({ dueDate: '2026-11-26', status: 'planned' }, '2026-11-27')).toBe(true)
    expect(isMilestoneOverdue({ dueDate: '2026-11-27', status: 'in_progress' }, '2026-11-27')).toBe(false)
    expect(isMilestoneOverdue({ dueDate: '2026-11-01', status: 'done' }, '2026-11-27')).toBe(false)
    expect(isMilestoneOverdue({ dueDate: '2026-11-01', status: 'cancelled' }, '2026-11-27')).toBe(false)
    expect(isMilestoneOverdue({ dueDate: null, status: 'planned' }, '2026-11-27')).toBe(false)
  })

  it('adds days to a date across months and years', () => {
    expect(addDays('2026-11-27', 56)).toBe('2027-01-22')
    expect(addDays('2026-11-27', 0)).toBe('2026-11-27')
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29')
  })

  it('suggests the template’s status on go and no-go – a known, allowed one the project isn’t in yet', () => {
    const statuses = new Map(
      [
        status('mvp'),
        status('stopped', { category: 'closed' }),
        status('old', { isArchived: true }),
        status('idea-selected', { itemType: 'idea' }),
      ].map((s) => [s.id, s]),
    )
    const gate = { kind: 'gate' as const, onGoStatusId: 'mvp', onNoGoStatusId: 'stopped' }
    const project = { statusId: 'poc' }
    expect(suggestedStatus(gate, 'go', project, statuses, [])?.id).toBe('mvp')
    expect(suggestedStatus(gate, 'no_go', project, statuses, [])?.id).toBe('stopped')
    for (const other of ['iterate', 'pivot', 'park', 'pending'] as const) {
      expect(suggestedStatus(gate, other, project, statuses, [])).toBeNull()
    }
    // Already there; not allowed by the template; unknown, archived or another type's.
    expect(suggestedStatus(gate, 'go', { statusId: 'mvp' }, statuses, [])).toBeNull()
    expect(suggestedStatus(gate, 'go', project, statuses, ['poc', 'stopped'])).toBeNull()
    expect(suggestedStatus(gate, 'no_go', project, statuses, ['poc', 'stopped'])?.id).toBe('stopped')
    expect(suggestedStatus({ ...gate, onGoStatusId: 'gone' }, 'go', project, statuses, [])).toBeNull()
    expect(suggestedStatus({ ...gate, onGoStatusId: 'old' }, 'go', project, statuses, [])).toBeNull()
    expect(suggestedStatus({ ...gate, onGoStatusId: 'idea-selected' }, 'go', project, statuses, [])).toBeNull()
    expect(suggestedStatus({ ...gate, kind: 'milestone' }, 'go', project, statuses, [])).toBeNull()
  })
})
