import { describe, expect, it } from 'vitest'
import { compareValues } from '@/composables/useLocalTable'
import type { AssignmentRow, DodItemRow, ItemRow, ItemShareRow, MilestoneRow } from '@/local/rows'
import {
  cleanGateFilters,
  countGateFilters,
  emptyGateContext,
  gateSortValue,
  gateText,
  matchGate,
  readGateFilters,
  visibleGateColumns,
  type GateContext,
} from './gateList'

const synced = {
  seq: 1,
  rev: 1,
  deleted: false,
  deleteBatchId: null,
  createdAt: '2026-11-01T00:00:00Z',
  createdBy: null,
  updatedAt: '2026-11-01T00:00:00Z',
  updatedBy: null,
}

const project = (id: string, number: number, extra: Partial<ItemRow> = {}) =>
  ({ ...synced, id, type: 'project', number, title: `Project ${id}`, projectTemplateId: null, ...extra }) as ItemRow

function gate(id: string, itemId: string, extra: Partial<MilestoneRow> = {}): MilestoneRow {
  return {
    ...synced,
    id,
    itemId,
    kind: 'gate',
    title: `Gate ${id}`,
    description: null,
    dueDate: null,
    status: 'planned',
    completedAt: null,
    decision: 'pending',
    decisionNote: null,
    decidedAt: null,
    decidedBy: null,
    onGoStatusId: null,
    onNoGoStatusId: null,
    color: null,
    sortOrder: 'a0',
    ...extra,
  }
}

const dod = (milestoneId: string, state: DodItemRow['state']) =>
  ({ ...synced, id: `${milestoneId}-${state}-${Math.random()}`, milestoneId, state }) as DodItemRow

const gates = [
  gate('g1', 'p1', {
    decision: 'go',
    decidedAt: '2026-11-20T10:00:00.000000Z',
    decidedBy: 'u1',
    dueDate: '2026-11-15',
  }),
  gate('g2', 'p1', { decision: 'no_go', decidedAt: '2026-11-25T10:00:00.000000Z', decidedBy: 'u2' }),
  gate('g3', 'p2', { decision: 'pending', dueDate: '2026-11-10', decisionNote: 'Waiting for the data' }),
  gate('g4', 'p2', { decision: 'iterate', decidedAt: null, updatedAt: '2026-11-26T08:00:00.000000Z' }),
  gate('m1', 'p1', { kind: 'milestone' }),
  gate('gone', 'p1', { deleted: true }),
  gate('elsewhere', 'p-unknown'),
]

const ctx: GateContext = emptyGateContext({
  projects: new Map([
    ['p1', project('p1', 7, { projectTemplateId: 't-ai', title: 'Invoice bot' })],
    ['p2', project('p2', 8)],
  ]),
  dod: new Map([['g1', [dod('g1', 'done'), dod('g1', 'na'), dod('g1', 'open')]]]),
  assignments: new Map([['g3', [{ ...synced, id: 'a1', milestoneId: 'g3', personId: 'me' } as AssignmentRow]]]),
  shares: new Map([['p2', [{ ...synced, id: 's1', itemId: 'p2', groupId: 'grp-rb', access: 'edit' } as ItemShareRow]]]),
  userName: (id) => ({ u1: 'Ada', u2: 'Ben' })[id ?? ''] ?? '',
  me: { personId: 'me' },
  today: '2026-11-27',
})

const ids = (f: Parameters<typeof matchGate>[1]) => gates.filter((g) => matchGate(g, f, ctx)).map((g) => g.id)

describe('the gate decisions list (docs/01 §11)', () => {
  it('lists the live gates of projects on the device', () => {
    expect(ids({})).toEqual(['g1', 'g2', 'g3', 'g4'])
  })

  it('filters by decision, template, shared group, assignment to me and overdue', () => {
    // The seeded Gate decisions view: every decision but pending.
    expect(ids({ decisions: ['go', 'no_go', 'iterate', 'pivot', 'park'] })).toEqual(['g1', 'g2', 'g4'])
    expect(ids({ decisions: ['pending'] })).toEqual(['g3'])
    expect(ids({ templateIds: ['t-ai'] })).toEqual(['g1', 'g2'])
    expect(ids({ groupIds: ['grp-rb'] })).toEqual(['g3', 'g4'])
    expect(ids({ assignedToMe: true })).toEqual(['g3'])
    // g1 is past due but decided and planned still: overdue counts the milestone status.
    expect(ids({ overdue: true })).toEqual(['g1', 'g3'])
    expect(ids({ overdue: true, decisions: ['pending'] })).toEqual(['g3'])
  })

  it('reads stored filters, dropping unknown members and values', () => {
    expect(
      readGateFilters({
        decisions: ['go', 'maybe'],
        statusIds: ['x'],
        assignedToMe: true,
        overdue: 'yes',
        groupIds: [],
      }),
    ).toEqual({ decisions: ['go'], assignedToMe: true })
    expect(cleanGateFilters({ decisions: [], overdue: false, templateIds: ['t'] })).toEqual({ templateIds: ['t'] })
    expect(countGateFilters({ decisions: ['go'], templateIds: ['t'], overdue: true }, ['decisions', 'overdue'])).toBe(1)
  })

  it('finds a gate by its title, its project’s key and title, and its note', () => {
    expect(gateText(gates[0]!, ctx)).toContain('P-0007')
    expect(gateText(gates[0]!, ctx)).toContain('Invoice bot')
    expect(gateText(gates[2]!, ctx)).toContain('Waiting for the data')
  })

  it('sorts newest decisions first, a decision not synced yet by its local time', () => {
    const decided = gates.filter((g) => matchGate(g, { decisions: ['go', 'no_go', 'iterate'] }, ctx))
    // `-decided_at` as useLocalTable sorts it.
    const sorted = [...decided].sort(
      (a, b) => -compareValues(gateSortValue(a, 'decided_at', ctx), gateSortValue(b, 'decided_at', ctx)),
    )
    expect(sorted.map((g) => g.id)).toEqual(['g4', 'g2', 'g1'])
    expect(gateSortValue(gates[2]!, 'decided_at', ctx)).toBeNull()
    expect(gateSortValue(gates[0]!, 'dod_progress', ctx)).toBe(0.5)
    expect(gateSortValue(gates[1]!, 'dod_progress', ctx)).toBeNull()
    expect(gateSortValue(gates[0]!, 'decided_by', ctx)).toBe('Ada')
    expect(gateSortValue(gates[2]!, 'decided_by', ctx)).toBeNull()
    expect(gateSortValue(gates[0]!, 'unknown', ctx)).toBeUndefined()
  })

  it('shows project and gate always, the known columns in the chosen order', () => {
    expect(visibleGateColumns(['decision', 'nope', 'decision']).map((c) => c.id)).toEqual([
      'project',
      'title',
      'decision',
    ])
    expect(visibleGateColumns(null).map((c) => c.id)).toContain('decided_at')
  })
})
