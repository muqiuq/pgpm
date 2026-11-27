import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { memoryDb, until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import { createItem } from './items'
import {
  addDodItem,
  applySuggestedStatus,
  assignToMilestone,
  createMilestone,
  decideGate,
  deleteMilestone,
  dodItemsOf,
  milestoneAssignmentsOf,
  milestonesOf,
  moveDodItem,
  moveMilestone,
  removeDodItem,
  renameDodItem,
  setDodState,
  unassignFromMilestone,
  updateMilestone,
} from './milestones'
import { Repo } from './repo'
import type { ItemRow, MilestoneRow } from './rows'

const ANA = '01970000-0000-7000-8000-00000000a001'
const BEN = '01970000-0000-7000-8000-00000000b001'
const G1 = '01970000-0000-7000-8000-000000000001'
const P1 = '01970000-0000-7000-8000-0000000000f1'

describe('milestones, gates and their DoD on the device (docs/01 §11)', () => {
  let db: ReturnType<typeof memoryDb>
  let repo: Repo
  let project: ItemRow

  beforeEach(async () => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => ANA })
    project = await repo.transaction((tx) =>
      createItem(tx, { type: 'project', title: 'Chat bot', statusId: 's-poc', shares: [] }),
    )
    await db.outbox.clear()
  })
  afterEach(async () => {
    await db.delete()
  })

  const titles = async () => (await milestonesOf(db, project.id)).map((m) => m.title)

  it('creates a gate with its DoD in one batch, at the end or after another', async () => {
    const g0 = await createMilestone(repo, project.id, {
      kind: 'gate',
      title: ' G0 Ready ',
      dueDate: '2026-12-01',
      dod: ['Key persons', '', 'Charter'],
      onGoStatusId: 's-poc',
      onNoGoStatusId: 's-stopped',
    })
    expect(g0).toMatchObject({ kind: 'gate', title: 'G0 Ready', status: 'planned', decision: 'pending' })
    const outbox = await db.outbox.toArray()
    expect(outbox.map((e) => `${e.op} ${e.table}`)).toEqual([
      'insert milestones',
      'insert dod_items',
      'insert dod_items',
    ])
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    // The server sets completedAt / decidedAt / decidedBy: never pushed.
    expect(outbox[0]!.row).not.toHaveProperty('decidedAt')
    expect((await dodItemsOf(db, g0.id)).map((d) => d.text)).toEqual(['Key persons', 'Charter'])

    const m2 = await createMilestone(repo, project.id, { kind: 'milestone', title: 'M2' })
    expect(m2.decision).toBeNull()
    expect(m2.onGoStatusId).toBeNull()
    await createMilestone(repo, project.id, { kind: 'milestone', title: 'M1', after: g0.id })
    await createMilestone(repo, project.id, { kind: 'milestone', title: 'Kick-off', after: null })
    expect(await titles()).toEqual(['Kick-off', 'G0 Ready', 'M1', 'M2'])
    await expect(createMilestone(repo, project.id, { kind: 'gate', title: ' ' })).rejects.toThrow('title')
  })

  it('moves milestones and DoD criteria by patching only the moved row’s key', async () => {
    const a = await createMilestone(repo, project.id, { kind: 'milestone', title: 'A', dod: ['one', 'two', 'three'] })
    await createMilestone(repo, project.id, { kind: 'milestone', title: 'B' })
    const c = await createMilestone(repo, project.id, { kind: 'gate', title: 'C' })
    await db.outbox.clear()
    expect(await moveMilestone(repo, project.id, c.id, -1)).toBe(true)
    expect(await titles()).toEqual(['A', 'C', 'B'])
    expect((await db.outbox.toArray()).map((e) => [e.table, e.id])).toEqual([['milestones', c.id]])
    expect(await moveMilestone(repo, project.id, a.id, -1)).toBe(false)

    const [one, , three] = await dodItemsOf(db, a.id)
    await moveDodItem(repo, three!.id, -1)
    await moveDodItem(repo, one!.id, 1)
    expect((await dodItemsOf(db, a.id)).map((d) => d.text)).toEqual(['three', 'one', 'two'])
  })

  it('keeps the DoD rules: n/a needs a note, texts are trimmed, criteria go', async () => {
    const m = await createMilestone(repo, project.id, { kind: 'milestone', title: 'M' })
    const item = await addDodItem(repo, m.id, ' Test data ')
    expect(item).toMatchObject({ text: 'Test data', state: 'open', note: null })
    await expect(setDodState(repo, item.id, 'na')).rejects.toThrow('note')
    await setDodState(repo, item.id, 'na', ' No personal data ')
    expect(await db.synced('dod_items').get(item.id)).toMatchObject({ state: 'na', note: 'No personal data' })
    // The note stays when only the state changes.
    await setDodState(repo, item.id, 'done')
    expect(await db.synced('dod_items').get(item.id)).toMatchObject({ state: 'done', note: 'No personal data' })
    await renameDodItem(repo, item.id, 'Test data ready')
    await expect(addDodItem(repo, m.id, '')).rejects.toThrow('criterion')
    await removeDodItem(repo, item.id)
    expect(await dodItemsOf(db, m.id)).toEqual([])
  })

  it('records a gate decision with its note in one patch and moves the project on confirmation', async () => {
    const gate = await createMilestone(repo, project.id, {
      kind: 'gate',
      title: 'G1',
      onGoStatusId: 's-mvp',
      onNoGoStatusId: 's-stopped',
    })
    await db.outbox.clear()
    await decideGate(repo, gate.id, 'go', '  Strong results ')
    const [patch] = await db.outbox.toArray()
    expect(patch!.changes).toEqual({
      decision: { old: 'pending', new: 'go' },
      decisionNote: { old: null, new: 'Strong results' },
    })
    await applySuggestedStatus(repo, project.id, 's-mvp')
    expect(await db.synced('items').get(project.id)).toMatchObject({ statusId: 's-mvp' })

    const plain = await createMilestone(repo, project.id, { kind: 'milestone', title: 'M' })
    await expect(decideGate(repo, plain.id, 'go', null)).rejects.toThrow('Only gates')
    await updateMilestone(repo, gate.id, { status: 'done' })
    expect(await db.synced('milestones').get(gate.id)).toMatchObject({ status: 'done' })
  })

  it('assigns persons once per milestone – the deciders of a gate as Review board – and deletes with children', async () => {
    const gate = await createMilestone(repo, project.id, { kind: 'gate', title: 'G2', dod: ['x'] })
    expect(await assignToMilestone(repo, gate.id, P1)).toBe('added')
    expect(await assignToMilestone(repo, gate.id, P1)).toBe('unchanged')
    expect(await assignToMilestone(repo, gate.id, P1, 'Sponsor')).toBe('updated')
    const [a] = await milestoneAssignmentsOf(db, gate.id)
    expect(a).toMatchObject({ itemId: null, milestoneId: gate.id, personId: P1, role: 'Sponsor' })
    const plain = await createMilestone(repo, project.id, { kind: 'milestone', title: 'M' })
    await assignToMilestone(repo, plain.id, P1)
    expect((await milestoneAssignmentsOf(db, plain.id))[0]!.role).toBeNull()
    await unassignFromMilestone(repo, (await milestoneAssignmentsOf(db, plain.id))[0]!.id)
    expect(await milestoneAssignmentsOf(db, plain.id)).toEqual([])

    await deleteMilestone(repo, gate.id)
    expect(await milestonesOf(db, project.id)).toHaveLength(1)
    expect(await dodItemsOf(db, gate.id)).toEqual([])
    expect(await milestoneAssignmentsOf(db, gate.id)).toEqual([])
  })

  it('refuses changes on a project the user may only read, and on other types', async () => {
    const m = await createMilestone(repo, project.id, { kind: 'milestone', title: 'M' })
    await db.synced('items').put({ ...project, access: 'read' })
    await expect(createMilestone(repo, project.id, { kind: 'gate', title: 'G' })).rejects.toThrow('read')
    await expect(updateMilestone(repo, m.id, { title: 'N' })).rejects.toThrow('read')
    const idea = await repo.transaction((tx) =>
      createItem(tx, { type: 'idea', title: 'I', statusId: 's-idea', shares: [] }),
    )
    await expect(createMilestone(repo, idea.id, { kind: 'gate', title: 'G' })).rejects.toThrow('projects')
  })
})

describe('milestones through the sync', () => {
  let server: FakeSyncServer
  let clients: TestClient[]

  beforeEach(() => {
    server = new FakeSyncServer()
    server.addUser({ id: ANA, name: 'Ana', groupIds: [G1] })
    server.addUser({ id: BEN, name: 'Ben', groupIds: [G1] })
    server.addGroup(G1, 'Innovation team')
    server.seed('persons', { id: P1, displayName: 'Petra', isActive: true, source: 'manual' })
    server.seed('statuses', {
      id: 's-poc',
      itemType: 'project',
      name: 'Prototype / PoC',
      color: '#0ea5e9',
      icon: null,
      category: 'active',
      sortOrder: 10,
      isDefault: true,
      maxDays: 84,
      isArchived: false,
    })
    clients = []
  })
  afterEach(async () => {
    for (const c of clients) await c.engine.stop()
    for (const c of clients) await c.db.delete()
  })

  it('plans and decides a gate offline; Ben sees it with its DoD and deciders', async () => {
    const ana = testClient(server, ANA)
    clients.push(ana)
    ana.engine.start()
    await until(() => ana.engine.status.connection === 'live', 'live')
    const project = await ana.repo.transaction((tx) =>
      createItem(tx, { type: 'project', title: 'P', statusId: 's-poc', shares: [{ groupId: G1, access: 'edit' }] }),
    )
    await until(async () => (await ana.db.outbox.count()) === 0, 'the project on the server')

    ana.goOffline()
    const gate = await createMilestone(ana.repo, project.id, { kind: 'gate', title: 'G1', dod: ['Result'] })
    const [criterion] = await dodItemsOf(ana.db, gate.id)
    await setDodState(ana.repo, criterion!.id, 'done')
    await assignToMilestone(ana.repo, gate.id, P1)
    await decideGate(ana.repo, gate.id, 'iterate', 'One more round')
    ana.goOnline()
    await until(async () => (await ana.db.outbox.count()) === 0, 'the gate on the server')
    expect(server.table('milestones').get(gate.id)).toMatchObject({
      decision: 'iterate',
      decisionNote: 'One more round',
    })

    const ben = testClient(server, BEN)
    clients.push(ben)
    ben.engine.start()
    await until(async () => !!(await ben.db.synced('milestones').get(gate.id)), 'the gate at Ben')
    expect((await ben.db.synced('milestones').get(gate.id)) as MilestoneRow).toMatchObject({ decision: 'iterate' })
    await until(async () => (await dodItemsOf(ben.db, gate.id)).length === 1, 'the DoD at Ben')
    expect((await dodItemsOf(ben.db, gate.id))[0]).toMatchObject({ state: 'done' })
    await until(async () => (await milestoneAssignmentsOf(ben.db, gate.id)).length === 1, 'the deciders at Ben')
    expect((await milestoneAssignmentsOf(ben.db, gate.id))[0]).toMatchObject({ personId: P1, role: 'Review board' })
  })
})
