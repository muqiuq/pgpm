import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { memoryDb, until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import {
  addLink,
  assignPerson,
  bulkAssign,
  bulkPatch,
  bulkRemove,
  convertedProject,
  convertToProject,
  createItem,
  daysInStatus,
  defaultStatus,
  isOverdue,
  overdueReason,
  selectableStatuses,
  statusesOf,
  titleError,
} from './items'
import { Repo } from './repo'
import type { ItemRow, StatusRow } from './rows'

const ANA = '01970000-0000-7000-8000-00000000a001'
const BEN = '01970000-0000-7000-8000-00000000b001'
const G1 = '01970000-0000-7000-8000-000000000001'

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, createdBy: null, updatedBy: null }
const at = '2026-11-01T00:00:00Z'

function status(id: string, itemType: StatusRow['itemType'], name: string, extra: Partial<StatusRow> = {}): StatusRow {
  return {
    ...synced,
    id,
    createdAt: at,
    updatedAt: at,
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

const statuses: StatusRow[] = [
  status('s-idea-new', 'idea', 'New', { isDefault: true, sortOrder: 10 }),
  status('s-idea-poc', 'idea', 'Elaborating', { sortOrder: 20, maxDays: 14 }),
  status('s-idea-selected', 'idea', 'Selected', { sortOrder: 50, category: 'closed' }),
  status('s-idea-old', 'idea', 'Old', { sortOrder: 5, isArchived: true }),
  status('s-lead-new', 'lead', 'New', { isDefault: true }),
  status('s-lead-won', 'lead', 'Won', { sortOrder: 40, category: 'closed' }),
  status('s-project-draft', 'project', 'Draft', { isDefault: true }),
  status('s-project-active', 'project', 'Active', { sortOrder: 20, category: 'active', maxDays: 84 }),
]

describe('statuses and their aging (docs/01 §5)', () => {
  it('orders a type’s statuses, defaults to the default one and offers the archived one only where it is set', () => {
    expect(statusesOf(statuses, 'idea').map((s) => s.name)).toEqual(['Old', 'New', 'Elaborating', 'Selected'])
    expect(defaultStatus(statuses, 'idea')?.id).toBe('s-idea-new')
    expect(
      defaultStatus(
        statuses.map((s) => ({ ...s, isDefault: false })),
        'idea',
      )?.id,
    ).toBe('s-idea-new')
    expect(selectableStatuses(statuses, 'idea').map((s) => s.name)).toEqual(['New', 'Elaborating', 'Selected'])
    expect(selectableStatuses(statuses, 'idea', 's-idea-old').map((s) => s.name)).toContain('Old')
    expect(defaultStatus(statuses, 'note')).toBeUndefined()
  })

  it('counts the days in a status and marks an item overdue beyond max days, never in a closed status', () => {
    const now = Date.parse('2026-11-27T12:00:00Z')
    const item = {
      type: 'idea' as const,
      createdAt: at,
      statusChangedAt: '2026-11-10T12:00:00Z',
      leadNextActionDue: null,
    }
    expect(daysInStatus(item, now)).toBe(17)
    expect(overdueReason(item, statuses[1], now)).toBe('17 days in Elaborating (at most 14)')
    expect(isOverdue(item, statuses[0], now)).toBe(false)
    expect(isOverdue({ ...item, statusChangedAt: null, createdAt: '2026-11-20T12:00:00Z' }, statuses[1], now)).toBe(
      false,
    )
    const lead = { type: 'lead' as const, createdAt: at, statusChangedAt: at, leadNextActionDue: '2026-11-26' }
    expect(overdueReason(lead, statuses[4], now)).toBe('Next action was due 2026-11-26')
    expect(isOverdue(lead, statuses[5], now)).toBe(false)
    expect(daysInStatus({ type: 'note', createdAt: at, statusChangedAt: null }, now)).toBeNull()
  })

  it('checks titles as the server does', () => {
    expect(titleError('  ')).toBe('A title is needed.')
    expect(titleError('x'.repeat(301))).toBe('At most 300 characters.')
    expect(titleError(' Fine ')).toBeNull()
  })
})

describe('items on the device: create, assign, link, bulk (docs/01 §4)', () => {
  let db: ReturnType<typeof memoryDb>
  let repo: Repo

  beforeEach(() => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => ANA })
  })
  afterEach(() => db.delete())

  it('creates an item with its status and shares in one batch; a lead gets CHF', async () => {
    const lead = await repo.transaction((tx) =>
      createItem(tx, {
        type: 'lead',
        title: ' ACME ',
        statusId: 's-lead-new',
        shares: [{ groupId: G1, access: 'edit' }],
      }),
    )
    const outbox = await db.outbox.toArray()
    expect(outbox.map((e) => e.table)).toEqual(['items', 'item_shares'])
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    expect(outbox[0]!.row).toMatchObject({ type: 'lead', title: 'ACME', statusId: 's-lead-new', leadCurrency: 'CHF' })
    expect(lead.ownerUserId).toBe(ANA)
    await expect(
      repo.transaction((tx) => createItem(tx, { type: 'idea', title: 'No status', shares: [] })),
    ).rejects.toThrow('There is no status')
  })

  it('assigns a person once per item (a second time changes the role), in order', async () => {
    const idea = await repo.transaction((tx) =>
      createItem(tx, { type: 'idea', title: 'Bot', statusId: 's-idea-new', shares: [] }),
    )
    expect(await repo.transaction((tx) => assignPerson(tx, idea.id, 'p1', 'Responsible'))).toBe('added')
    expect(await repo.transaction((tx) => assignPerson(tx, idea.id, 'p2', null))).toBe('added')
    expect(await repo.transaction((tx) => assignPerson(tx, idea.id, 'p1', 'Responsible'))).toBe('unchanged')
    expect(await repo.transaction((tx) => assignPerson(tx, idea.id, 'p1', ' Sponsor '))).toBe('updated')
    const rows = await db.synced('assignments').where('itemId').equals(idea.id).toArray()
    expect(rows.map((r) => [r.personId, r.role])).toEqual([
      ['p1', 'Sponsor'],
      ['p2', null],
    ])
    expect(String(rows[0]!.sortOrder) < String(rows[1]!.sortOrder)).toBe(true)
  })

  it('links two items once per relation, never an item to itself', async () => {
    const [a, b] = await repo.transaction(async (tx) => [
      await createItem(tx, { type: 'note', title: 'Lesson', shares: [] }),
      await createItem(tx, { type: 'project', title: 'Pilot', statusId: 's-project-draft', shares: [] }),
    ])
    expect(await repo.transaction((tx) => addLink(tx, a!.id, b!.id, 'learning_of'))).toBe(true)
    expect(await repo.transaction((tx) => addLink(tx, a!.id, b!.id, 'learning_of'))).toBe(false)
    expect(await repo.transaction((tx) => addLink(tx, a!.id, b!.id, 'related'))).toBe(true)
    await expect(repo.transaction((tx) => addLink(tx, a!.id, a!.id, 'related'))).rejects.toThrow('itself')
  })

  it('bulk actions: one batch, items without edit (or manage for delete) skipped', async () => {
    const mine = await repo.transaction((tx) =>
      createItem(tx, { type: 'idea', title: 'Mine', statusId: 's-idea-new', shares: [] }),
    )
    await db.synced('items').bulkPut([
      { ...mine, id: 'read', title: 'Read', access: 'read' },
      { ...mine, id: 'edit', title: 'Edit', access: 'edit' },
    ])
    await db.outbox.clear()
    const ids = [mine.id, 'read', 'edit', 'gone']
    expect(await bulkPatch(repo, ids, { statusId: 's-idea-poc' })).toEqual({ changed: 2, skipped: 2 })
    expect(new Set((await db.outbox.toArray()).map((e) => e.batch)).size).toBe(1)
    expect(await bulkAssign(repo, ids, 'p1', 'Contributor')).toEqual({ changed: 2, skipped: 2 })
    expect(await bulkRemove(repo, ids)).toEqual({ changed: 1, skipped: 3 })
    expect((await db.synced('items').get('edit'))?.statusId).toBe('s-idea-poc')
    expect((await db.synced('items').get('read'))?.statusId).toBe('s-idea-new')
  })
})

describe('idea / lead → project conversion (docs/01 §8, docs/03 §12.8)', () => {
  let server: FakeSyncServer
  let clients: TestClient[]

  beforeEach(() => {
    server = new FakeSyncServer()
    server.addUser({ id: ANA, name: 'Ana Muster', groupIds: [G1] })
    server.addUser({ id: BEN, name: 'Ben Beispiel', groupIds: [G1] })
    server.addGroup(G1, 'Innovation team')
    for (const s of statuses) server.seed('statuses', s)
    server.seed('persons', { id: 'p1', displayName: 'Petra', isActive: true, source: 'manual' })
    clients = []
  })
  afterEach(async () => {
    for (const c of clients) await c.engine.stop()
    for (const c of clients) await c.db.delete()
  })

  function client(userId: string) {
    const c = testClient(server, userId)
    clients.push(c)
    return c
  }

  it('converts offline in one batch – shares, persons, links, origin, cover – and the server marks the source', async () => {
    const ana = client(ANA)
    ana.engine.start()
    await until(() => ana.engine.status.connection === 'live', 'live')
    const { idea, target } = await ana.repo.transaction(async (tx) => {
      const idea = await createItem(tx, {
        type: 'idea',
        title: 'Chat bot',
        statusId: 's-idea-new',
        shares: [{ groupId: G1, access: 'edit' }],
        values: { description: 'Answers questions', color: '#16a34a', imageFileId: 'f-cover' },
      })
      const target = await createItem(tx, {
        type: 'project',
        title: 'Ambition',
        statusId: 's-project-draft',
        shares: [],
      })
      await assignPerson(tx, idea.id, 'p1', 'Responsible')
      await addLink(tx, idea.id, target.id, 'contributes_to')
      return { idea, target }
    })
    await until(async () => (await ana.db.outbox.count()) === 0, 'the idea on the server')

    ana.goOffline()
    const result = await convertToProject(ana.repo, idea.id, statuses)
    const project = result.project
    expect(result.sourceStatus?.name).toBe('Selected')
    expect(result.copied).toEqual({ shares: 1, assignments: 1, links: 1 })
    expect(project).toMatchObject({
      type: 'project',
      title: 'Chat bot',
      description: 'Answers questions',
      color: '#16a34a',
      imageFileId: 'f-cover',
      originItemId: idea.id,
      statusId: 's-project-draft',
    })
    const outbox = await ana.db.outbox.toArray()
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    expect(outbox.map((e) => `${e.op} ${e.table}`)).toEqual([
      'insert items',
      'insert item_shares',
      'insert assignments',
      'insert item_links',
      'insert item_links',
      'patch items',
    ])
    expect(outbox.find((e) => e.op === 'patch')?.changes).toEqual({
      statusId: { old: 's-idea-new', new: 's-idea-selected' },
    })
    // Converted already on the device, before the server says so.
    expect((await convertedProject(ana.db, (await ana.db.synced('items').get(idea.id)) as ItemRow))?.id).toBe(
      project.id,
    )
    await expect(convertToProject(ana.repo, idea.id, statuses)).rejects.toThrow('converted already')

    ana.goOnline()
    await until(async () => (await ana.db.outbox.count()) === 0, 'the conversion on the server')
    expect(server.table('items').get(idea.id)).toMatchObject({
      convertedToItemId: project.id,
      statusId: 's-idea-selected',
    })
    expect(server.table('items').get(project.id)).toMatchObject({ number: 2, originItemId: idea.id })
    await until(
      async () => (await ana.db.synced('items').get(idea.id))?.convertedToItemId === project.id,
      'the source marked at Ana',
    )
    const links = [...server.table('item_links').values()].filter((l) => l.fromItemId === project.id)
    expect(links.map((l) => [l.toItemId, l.relation]).sort()).toEqual(
      [
        [idea.id, 'related'],
        [target.id, 'contributes_to'],
      ].sort(),
    )

    // Ben (same group) sees the project with its origin.
    const ben = client(BEN)
    ben.engine.start()
    await until(async () => !!(await ben.db.synced('items').get(project.id)), 'the project at Ben')
    expect(await ben.db.synced('items').get(project.id)).toMatchObject({ access: 'edit', originItemId: idea.id })
  })

  it('refuses what can’t be converted: a project, read access, no project status', async () => {
    const db = memoryDb()
    const repo = new Repo({ db: () => db, userId: () => ANA })
    const idea = await repo.transaction((tx) =>
      createItem(tx, { type: 'idea', title: 'Read only', statusId: 's-idea-new', shares: [] }),
    )
    await expect(
      convertToProject(
        repo,
        idea.id,
        statuses.filter((s) => s.itemType !== 'project'),
      ),
    ).rejects.toThrow('no project status')
    await db.synced('items').put({ ...idea, access: 'read' })
    await expect(convertToProject(repo, idea.id, statuses)).rejects.toThrow('not convert')
    const project = await repo.transaction((tx) =>
      createItem(tx, { type: 'project', title: 'P', statusId: 's-project-draft', shares: [] }),
    )
    await expect(convertToProject(repo, project.id, statuses)).rejects.toThrow('Only ideas and leads')
    await db.delete()
  })
})
