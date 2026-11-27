import { describe, expect, it } from 'vitest'
import type { AssignmentRow, ItemLinkRow, ItemRow, ItemShareRow, ItemType, StatusRow } from '@/local/rows'
import {
  cleanFilters,
  columnsFor,
  countFilters,
  defaultColumns,
  emptyContext,
  groupBy,
  linkSummary,
  matchItem,
  readFilters,
  sortValue,
  visibleColumns,
  type ItemFilters,
  type ListContext,
} from './itemList'

const now = Date.UTC(2026, 10, 27, 12)
const days = (n: number) => new Date(now - n * 24 * 60 * 60 * 1000).toISOString()

let seq = 0
function item(type: ItemType, extra: Partial<ItemRow> = {}): ItemRow {
  seq++
  return {
    id: `i${seq}`,
    seq,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: days(10),
    createdBy: 'u-ana',
    updatedAt: days(1),
    updatedBy: 'u-ana',
    type,
    number: seq,
    title: `Item ${seq}`,
    description: null,
    color: null,
    imageFileId: null,
    ownerUserId: 'u-ana',
    statusId: null,
    statusChangedAt: null,
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
    access: 'manage',
    ...extra,
  }
}

function status(id: string, itemType: StatusRow['itemType'], extra: Partial<StatusRow> = {}): StatusRow {
  return {
    id,
    seq: 1,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: days(100),
    createdBy: null,
    updatedAt: days(100),
    updatedBy: null,
    itemType,
    name: id,
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
const link = (fromItemId: string, toItemId: string, relation: ItemLinkRow['relation']): ItemLinkRow => ({
  ...synced,
  id: `l-${fromItemId}-${toItemId}`,
  fromItemId,
  toItemId,
  relation,
})

describe('item lists (docs/01 §4.3): filters, columns, sorting', () => {
  const statuses = [
    status('new', 'idea', { sortOrder: 10 }),
    status('poc', 'project', { sortOrder: 20, category: 'active', maxDays: 84, name: 'Prototype / PoC' }),
    status('done', 'project', { sortOrder: 30, category: 'closed' }),
    status('lead-new', 'lead'),
  ]
  const idea = item('idea', { statusId: 'new', color: '#dc2626', title: 'Chatbot', createdAt: days(30) })
  const poc = item('project', { statusId: 'poc', statusChangedAt: days(90), projectTemplateId: 't1' })
  const fresh = item('project', { statusId: 'poc', statusChangedAt: days(3), ownerUserId: 'u-ben' })
  const closed = item('project', { statusId: 'done', statusChangedAt: days(200) })
  const lead = item('lead', { statusId: 'lead-new', statusChangedAt: days(1), leadNextActionDue: '2026-11-20' })
  const learning = item('note', { pinned: true })
  const plainNote = item('note')
  const all = [idea, poc, fresh, closed, lead, learning, plainNote]
  const assignments: AssignmentRow[] = [
    {
      ...synced,
      id: 'a1',
      itemId: idea.id,
      milestoneId: null,
      personId: 'p-ana',
      role: 'Responsible',
      sortOrder: 'a0',
    },
    { ...synced, id: 'a2', itemId: poc.id, milestoneId: null, personId: 'p-ben', role: null, sortOrder: 'a0' },
  ]
  const shares: ItemShareRow[] = [{ ...synced, id: 's1', itemId: fresh.id, groupId: 'g-board', access: 'read' }]
  const links = [link(learning.id, poc.id, 'learning_of'), link(plainNote.id, idea.id, 'related')]
  const ctx: ListContext = emptyContext({
    statuses: new Map(statuses.map((s) => [s.id, s])),
    assignments: groupBy(assignments, (a) => a.itemId),
    shares: groupBy(shares, (s) => s.itemId),
    outgoing: groupBy(links, (l) => l.fromItemId),
    incoming: groupBy(links, (l) => l.toItemId),
    types: new Map(all.map((i) => [i.id, i.type])),
    persons: new Map([
      ['p-ana', { displayName: 'Ana Muster' } as never],
      ['p-ben', { displayName: 'Ben Beispiel' } as never],
    ]),
    userName: (id) => ({ 'u-ana': 'Ana Muster', 'u-ben': 'Ben Beispiel' })[id ?? ''] ?? '',
    templates: new Map([['t1', 'AI use case']]),
    me: { userId: 'u-ana', personId: 'p-ana' },
    now,
  })
  const ids = (f: ItemFilters) => all.filter((i) => matchItem(i, f, ctx)).map((i) => i.id)

  it('filters by status, category, closed, template and owner', () => {
    expect(ids({ statusIds: ['poc'] })).toEqual([poc.id, fresh.id])
    expect(ids({ categories: ['closed'] })).toEqual([closed.id])
    expect(ids({ hideClosed: true })).not.toContain(closed.id)
    expect(ids({ templateIds: ['t1'] })).toEqual([poc.id])
    expect(ids({ ownerIds: ['u-ben'] })).toEqual([fresh.id])
    expect(ids({ mine: true })).not.toContain(fresh.id)
  })

  it('filters assigned to me (via the user’s person), by assigned person and shared with a group', () => {
    expect(ids({ assignedToMe: true })).toEqual([idea.id])
    expect(
      all.filter((i) => matchItem(i, { assignedToMe: true }, { ...ctx, me: { userId: 'u', personId: null } })),
    ).toEqual([])
    expect(ids({ personIds: ['p-ben', 'p-ana'] })).toEqual([idea.id, poc.id])
    expect(ids({ groupIds: ['g-board'] })).toEqual([fresh.id])
  })

  it('filters overdue: longer in the status than its max days, or a lead’s next action due; closed never', () => {
    expect(ids({ overdue: true })).toEqual([poc.id, lead.id])
  })

  it('filters by colour, pinned and created/modified ranges (inclusive days)', () => {
    expect(ids({ colors: ['#dc2626'] })).toEqual([idea.id])
    expect(ids({ pinned: true })).toEqual([learning.id])
    const created = days(30).slice(0, 10)
    expect(ids({ createdFrom: created, createdTo: created })).toEqual([idea.id])
    expect(ids({ modifiedTo: '2026-01-01' })).toEqual([])
  })

  it('works the seeded view Learnings: notes with an outgoing learning_of link to a project or idea', () => {
    const learnings = readFilters({
      hasLink: { relation: 'learning_of', direction: 'outgoing', targetTypes: ['project', 'idea'] },
    })
    expect(ids(learnings)).toEqual([learning.id])
    expect(ids({ hasLink: { relation: 'learning_of', direction: 'incoming' } })).toEqual([poc.id])
    expect(ids({ hasLink: { relation: 'related', direction: 'any' } })).toEqual([idea.id, plainNote.id])
    expect(ids({ hasLink: { relation: 'learning_of', targetTypes: ['lead'] } })).toEqual([])
    // A target not on this device (restricted) still counts.
    const restricted = { ...ctx, types: new Map() }
    expect(all.filter((i) => matchItem(i, learnings, restricted)).map((i) => i.id)).toEqual([learning.id])
  })

  it('reads stored filters strictly and counts and cleans them', () => {
    expect(
      readFilters({ statusIds: ['a', 3], overdue: 'yes', createdFrom: 'soon', unknown: 1, fields: [{ op: 'eq' }] }),
    ).toEqual({ statusIds: ['a'], fields: [{ op: 'eq' }] })
    expect(cleanFilters({ statusIds: [], pinned: false, mine: true })).toEqual({ mine: true })
    expect(countFilters({ statusIds: ['a'], overdue: true, colors: [] }, ['statusIds'])).toBe(1)
    expect(ids({ fields: [{ field: 'field:x', op: 'set' }] })).toHaveLength(all.length)
  })

  it('has the check-packages column ids per type; unknown ids (field:…) are ignored, key and title kept', () => {
    expect(
      columnsFor('note')
        .map((c) => c.id)
        .sort(),
    ).toEqual(['key', 'title', 'owner', 'persons', 'color', 'created_at', 'updated_at', 'pinned', 'links'].sort())
    expect(columnsFor('lead').map((c) => c.id)).toContain('next_action_due')
    expect(columnsFor('project').map((c) => c.id)).toEqual(
      expect.arrayContaining(['days_in_status', 'template', 'start_date', 'end_date', 'links']),
    )
    expect(defaultColumns('note')).toEqual(['key', 'title', 'owner', 'updated_at'])
    expect(visibleColumns('note', ['key', 'title', 'links', 'owner', 'updated_at']).map((c) => c.id)).toEqual([
      'key',
      'title',
      'links',
      'owner',
      'updated_at',
    ])
    expect(visibleColumns('idea', ['field:impact_score', 'status']).map((c) => c.id)).toEqual([
      'key',
      'title',
      'status',
    ])
  })

  it('sorts by key number, status order, names, days in status; unknown columns not at all', () => {
    const projects = [closed, fresh, poc]
    const by = (id: string) =>
      [...projects].sort((a, b) => {
        const x = sortValue(a, id, ctx) as number
        const y = sortValue(b, id, ctx) as number
        return x < y ? -1 : x > y ? 1 : 0
      })
    expect(by('key').map((i) => i.id)).toEqual([poc.id, fresh.id, closed.id])
    expect(by('status').map((i) => i.id)).toEqual([fresh.id, poc.id, closed.id])
    expect(sortValue(poc, 'days_in_status', ctx)).toBe(90)
    expect(sortValue(fresh, 'owner', ctx)).toBe('Ben Beispiel')
    expect(sortValue(idea, 'persons', ctx)).toBe('Ana Muster')
    expect(sortValue(poc, 'template', ctx)).toBe('AI use case')
    expect(sortValue(poc, 'field:impact_score', ctx)).toBeUndefined()
    expect(linkSummary(poc, ctx)).toEqual({ count: 1, text: 'has learning' })
  })
})
