import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { memoryDb, until } from '@/test/memoryDb'
import { testClient, type TestClient } from '@/test/syncClient'
import type { ItemRow } from './rows'

const ANA = '01970000-0000-7000-8000-00000000a001'
const BEN = '01970000-0000-7000-8000-00000000b001'
const G1 = '01970000-0000-7000-8000-000000000001'
const G2 = '01970000-0000-7000-8000-000000000002'

describe('the sync engine against the contract (docs/03 §11.4–§11.8)', () => {
  let server: FakeSyncServer
  let clients: TestClient[]

  beforeEach(() => {
    server = new FakeSyncServer()
    server.addUser({ id: ANA, name: 'Ana Muster', groupIds: [G1] })
    server.addUser({ id: BEN, name: 'Ben Beispiel', groupIds: [G1] })
    server.addGroup(G1, 'Innovation team')
    server.addGroup(G2, 'Review board')
    server.seed('persons', { id: 'p-ana', displayName: 'Ana Muster', userId: ANA, isActive: true, source: 'manual' })
    clients = []
  })

  afterEach(async () => {
    for (const c of clients) await c.engine.stop()
    for (const c of clients) await c.db.delete()
  })

  function client(userId: string, options: Parameters<typeof testClient>[2] = {}) {
    const c = testClient(server, userId, options)
    clients.push(c)
    return c
  }

  const live = (...cs: TestClient[]) =>
    until(() => cs.every((c) => c.engine.status.connection === 'live'), 'the live channel')

  /** Ana creates a note shared with G1 (one batch), and Ben has it. */
  async function sharedNote(ana: TestClient, ben: TestClient, title = 'Shared note') {
    const note = await ana.repo.transaction(async (tx) => {
      const n = await tx.create<ItemRow>('items', { type: 'note', title })
      await tx.create('item_shares', { itemId: n.id, groupId: G1, access: 'edit' })
      return n
    })
    await until(async () => (await ben.db.synced('items').get(note.id))?.title === title, 'the note at Ben')
    return note
  }

  const serverRow = (id: string) => server.table('items').get(id)!

  it('catches up over HTTP, goes live, and spreads a change to the other user within moments', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    expect(await ben.db.synced('groups').count()).toBe(2)
    expect(await ben.db.synced('persons').get('p-ana')).toMatchObject({ displayName: 'Ana Muster' })

    const started = Date.now()
    const note = await sharedNote(ana, ben, 'Hello')
    expect(Date.now() - started).toBeLessThan(1000)
    expect(await ben.db.synced('items').get(note.id)).toMatchObject({
      number: 1,
      access: 'edit',
      ownerUserId: ANA,
      _local: { serverRev: 1, pending: false },
    })
    await until(async () => (await ana.db.synced('items').get(note.id))?.number === 1, 'the number at Ana')
    expect(await ana.db.outbox.count()).toBe(0)
    const push = server.requests.find((r) => r.mutations.some((m) => m.id === note.id))!
    expect(push.pull).toBe(false)
    expect(push.mutations.map((m) => m.table)).toEqual(['items', 'item_shares'])
    expect(new Set(push.mutations.map((m) => m.batch)).size).toBe(1)

    await ben.repo.patch('items', note.id, { title: 'Hello, Ana' })
    await until(async () => (await ana.db.synced('items').get(note.id))?.title === 'Hello, Ana', 'Ben’s title at Ana')
    expect(ana.engine.status.cursor).toBe(server.seq)
  })

  it('offline edits: other fields apply, the same field becomes a conflict; Keep mine wins it back', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben)

    ben.goOffline()
    await until(() => ben.engine.status.connection === 'offline', 'Ben offline')
    await ana.repo.patch('items', note.id, { title: 'Ana’s title' })
    await until(() => serverRow(note.id).title === 'Ana’s title', 'Ana’s push')
    await ben.repo.patch('items', note.id, { title: 'Ben’s title', pinned: true })
    expect(await ben.db.outbox.count()).toBe(1)

    ben.goOnline()
    await until(async () => (await ben.db.conflicts.count()) === 1, 'the conflict')
    const [conflict] = await ben.db.conflicts.toArray()
    expect(conflict).toMatchObject({
      kind: 'field',
      table: 'items',
      rowId: note.id,
      title: 'Shared note',
      fields: { title: { mine: 'Ben’s title', theirs: 'Ana’s title', old: 'Shared note', by: 'Ana Muster' } },
    })
    expect(serverRow(note.id)).toMatchObject({ title: 'Ana’s title', pinned: true })
    await until(async () => (await ben.db.synced('items').get(note.id))?.pinned === true, 'the merged row')
    expect(await ben.db.synced('items').get(note.id)).toMatchObject({ title: 'Ana’s title', pinned: true })
    expect(await ben.db.outbox.count()).toBe(0)
    await until(() => ben.notices.some((n) => n.severity === 'warn' && n.summary === '1 sync conflict'), 'the toast')

    await ben.actions.keepMine(conflict!)
    await until(() => serverRow(note.id).title === 'Ben’s title', 'Keep mine on the server')
    await until(async () => (await ana.db.synced('items').get(note.id))?.title === 'Ben’s title', 'at Ana')
    await until(
      () => server.resolved.some((r) => r.userId === BEN && r.field === 'title' && r.resolution === 'mine'),
      'the resolution reported',
    )
    expect(await ben.db.conflicts.count()).toBe(0)
  })

  it('Keep theirs drops the change, Edit pushes the edited value over theirs, and "for all" does all', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const one = await sharedNote(ana, ben, 'One')
    const two = await sharedNote(ana, ben, 'Two')
    const three = await sharedNote(ana, ben, 'Three')

    ben.goOffline()
    await until(() => ben.engine.status.connection === 'offline', 'Ben offline')
    for (const n of [one, two, three]) await ana.repo.patch('items', n.id, { title: `${n.title} (Ana)` })
    await until(() => serverRow(three.id).title === 'Three (Ana)', 'Ana’s pushes')
    for (const n of [one, two, three]) await ben.repo.patch('items', n.id, { title: `${n.title} (Ben)` })
    ben.goOnline()
    await until(async () => (await ben.db.conflicts.count()) === 3, 'three conflicts')
    const byRow = async (id: string) => (await ben.db.conflicts.where('[table+rowId]').equals(['items', id]).first())!

    await ben.actions.keepTheirs(await byRow(one.id))
    expect(await ben.db.synced('items').get(one.id)).toMatchObject({ title: 'One (Ana)' })
    await ben.actions.edit(await byRow(two.id), { title: 'Two (both)' })
    await until(() => serverRow(two.id).title === 'Two (both)', 'the edited value')
    const failed = await ben.actions.keepTheirsForAll(await ben.db.conflicts.toArray())
    expect(failed).toEqual([])
    expect(await ben.db.conflicts.count()).toBe(0)
    await until(() => server.resolved.filter((r) => r.userId === BEN).length === 3, 'three resolutions reported')
    expect(server.resolved.map((r) => r.resolution).sort()).toEqual(['edited', 'theirs', 'theirs'])
    expect(serverRow(three.id).title).toBe('Three (Ana)')
  })

  it('deleted by someone else: the change becomes deleted_by_other; Restore with my changes brings it back', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben)

    ben.goOffline()
    await until(() => ben.engine.status.connection === 'offline', 'Ben offline')
    await ana.repo.remove('items', note.id)
    await until(() => serverRow(note.id).deleted === true, 'the delete')
    await ben.repo.patch('items', note.id, { title: 'Ben was here' })
    ben.goOnline()
    await until(async () => (await ben.db.conflicts.count()) === 1, 'the conflict')
    const [conflict] = await ben.db.conflicts.toArray()
    expect(conflict).toMatchObject({
      kind: 'deleted_by_other',
      rowId: note.id,
      title: 'Shared note',
      fields: { title: { mine: 'Ben was here' } },
    })
    expect(conflict!.deleteBatchId).toBeTruthy()
    expect(await ben.db.synced('items').get(note.id)).toMatchObject({ deleted: true, title: 'Shared note' })

    await ben.actions.restoreWithMine(conflict!)
    await until(() => !serverRow(note.id).deleted && serverRow(note.id).title === 'Ben was here', 'restored')
    await until(async () => (await ana.db.synced('items').get(note.id))?.title === 'Ben was here', 'at Ana')
    expect(await ana.db.synced('items').get(note.id)).toMatchObject({ deleted: false })
    expect(await ben.db.conflicts.count()).toBe(0)
  })

  it('deleting what someone else changed: changed_since_delete keeps it; Delete anyway forces the delete', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben)
    await ana.repo.transaction(async (tx) => tx.patch('items', note.id, { title: 'Ana kept working' }))
    await until(async () => (await ben.db.synced('items').get(note.id))?.title === 'Ana kept working', 'at Ben')

    // Ana (manage) deletes offline while Ben changes it.
    ana.goOffline()
    await until(() => ana.engine.status.connection === 'offline', 'Ana offline')
    await ana.repo.remove('items', note.id)
    expect(await ana.db.synced('items').get(note.id)).toMatchObject({ deleted: true })
    await ben.repo.patch('items', note.id, { description: 'Ben adds content' })
    await until(() => serverRow(note.id).description === 'Ben adds content', 'Ben’s change')
    ana.goOnline()
    await until(async () => (await ana.db.conflicts.count()) === 1, 'the delete conflict')
    const [conflict] = await ana.db.conflicts.toArray()
    expect(conflict).toMatchObject({ kind: 'changed_since_delete', rowId: note.id })
    expect(await ana.db.synced('items').get(note.id)).toMatchObject({
      deleted: false,
      description: 'Ben adds content',
    })
    expect(serverRow(note.id).deleted).toBe(false)

    await ana.actions.deleteAnyway(conflict!)
    await until(() => serverRow(note.id).deleted === true, 'the forced delete')
    expect(server.requests.some((r) => r.mutations.some((m) => m.op === 'delete' && m.force === true))).toBe(true)
    await until(async () => (await ben.db.synced('items').get(note.id))?.deleted === true, 'the tombstone at Ben')
  })

  it('unsharing evicts the note from the other device live, with its shares and drafts', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben, 'Secret')
    await until(async () => (await ben.db.synced('item_shares').where('itemId').equals(note.id).count()) === 1, 'share')
    await ben.db.drafts.put({ key: `items:${note.id}:description`, revision: 1, text: 'draft', savedAt: 'now' })

    server.putShares(ANA, note.id, [])
    await until(async () => !(await ben.db.synced('items').get(note.id)), 'the eviction')
    expect(await ben.db.synced('item_shares').where('itemId').equals(note.id).count()).toBe(0)
    expect(await ben.db.drafts.count()).toBe(0)
    await until(
      () => ben.notices.some((n) => n.summary === 'Access removed' && !!n.detail?.includes('Secret')),
      'the toast',
    )
    // The owner keeps it, re-stamped.
    expect(await ana.db.synced('items').get(note.id)).toMatchObject({ title: 'Secret', access: 'manage' })

    // Shared again: it comes back (re-stamp).
    server.putShares(ANA, note.id, [{ groupId: G1, access: 'read' }])
    await until(async () => (await ben.db.synced('items').get(note.id))?.access === 'read', 'shared again')
  })

  it('a reset (access epoch) pushes the outbox, empties the synced stores, keeps conflicts, pulls from 0', async () => {
    const ben = client(BEN)
    ben.engine.start()
    await live(ben)
    await ben.db.synced('items').put({ id: 'stale', type: 'note', title: 'Not visible any more', rev: 1 })
    await ben.db.conflicts.add({
      id: 'c1',
      table: 'items',
      rowId: 'x',
      mapId: null,
      kind: 'field',
      fields: {},
      baseRev: 1,
      createdAt: 'now',
    })
    ben.goOffline()
    await until(() => ben.engine.status.connection === 'offline', 'Ben offline')
    const mine = await ben.repo.create<ItemRow>('items', { type: 'note', title: 'Before the reset' })
    server.setGroups(BEN, [G2])
    ben.goOnline()
    await until(async () => (await ben.db.getMeta('accessEpoch', 0)) === 2, 'the new epoch')
    await until(async () => !(await ben.db.synced('items').get('stale')), 'the stale row gone')
    expect(server.table('items').get(mine.id)).toBeDefined()
    expect(await ben.db.synced('items').get(mine.id)).toMatchObject({ title: 'Before the reset', number: 1 })
    expect(await ben.db.conflicts.count()).toBe(1)
    expect(await ben.db.synced('groups').count()).toBe(2)
    await live(ben)
  })

  it('a reset message on the live channel, and close 4409, reset too', async () => {
    const ben = client(BEN)
    ben.engine.start()
    await live(ben)
    await ben.db.synced('items').put({ id: 'stale', type: 'note', title: 'Stale', rev: 1 })
    server.close(BEN, 4409)
    await until(async () => !(await ben.db.synced('items').get('stale')), 'reset after 4409')
    await live(ben)
    await ben.db.synced('items').put({ id: 'stale2', type: 'note', title: 'Stale', rev: 1 })
    const before = server.requests.length
    server.setGroups(BEN, [G1])
    await until(async () => !(await ben.db.synced('items').get('stale2')), 'reset after the message')
    await live(ben)
    // The message and its close 4409 are one reset: one pull from 0.
    expect(server.requests.slice(before).filter((r) => r.since === 0)).toHaveLength(1)
  })

  it('a patch whose every key conflicts is a conflict of kind field: nothing applied, the server’s row kept', async () => {
    const ana = client(ANA, { live: false })
    const ben = client(BEN, { live: false })
    const note = await ana.repo.transaction(async (tx) => {
      const n = await tx.create<ItemRow>('items', { type: 'note', title: 'Both' })
      await tx.create('item_shares', { itemId: n.id, groupId: G1, access: 'edit' })
      return n
    })
    await ana.engine.sync({ pull: true })
    await ben.engine.sync({ pull: true })
    await ben.repo.patch('items', note.id, { title: 'Ben’s' })
    await ana.repo.patch('items', note.id, { title: 'Ana’s', pinned: true })
    await ana.engine.sync({ pull: true })
    const result = await ben.engine.sync({ pull: true })
    expect(result).toMatchObject({ ok: true, conflicts: 1 })
    const [conflict] = await ben.db.conflicts.toArray()
    expect(conflict).toMatchObject({
      kind: 'field',
      fields: { title: { mine: 'Ben’s', theirs: 'Ana’s', by: 'Ana Muster' } },
    })
    expect(await ben.db.synced('items').get(note.id)).toMatchObject({ title: 'Ana’s', pinned: true })
    expect(serverRow(note.id)).toMatchObject({ title: 'Ana’s', rev: 2 })
  })

  it('a change of a row the user can no longer see is dropped (missing_access without current)', async () => {
    const ana = client(ANA, { live: false })
    const ben = client(BEN, { live: false })
    const note = await ana.repo.transaction(async (tx) => {
      const n = await tx.create<ItemRow>('items', { type: 'note', title: 'Soon private' })
      await tx.create('item_shares', { itemId: n.id, groupId: G1, access: 'edit' })
      return n
    })
    await ana.engine.sync({ pull: true })
    await ben.engine.sync({ pull: true })
    await ben.repo.patch('items', note.id, { title: 'Too late' })
    server.putShares(ANA, note.id, [])
    expect(await ben.engine.sync({ pull: true })).toMatchObject({ ok: true, rejected: 1 })
    expect(await ben.db.synced('items').get(note.id)).toBeUndefined()
    expect(await ben.db.outbox.count()).toBe(0)
    expect(serverRow(note.id).title).toBe('Soon private')
  })

  it('an eviction and the row again in one page: access came back, the row stays (evictions first)', async () => {
    const ana = client(ANA, { live: false })
    const ben = client(BEN, { live: false })
    const note = await ana.repo.transaction(async (tx) => {
      const n = await tx.create<ItemRow>('items', { type: 'note', title: 'Back again' })
      await tx.create('item_shares', { itemId: n.id, groupId: G1, access: 'edit' })
      return n
    })
    await ana.engine.sync({ pull: true })
    await ben.engine.sync({ pull: true })
    server.putShares(ANA, note.id, [])
    server.putShares(ANA, note.id, [{ groupId: G1, access: 'read' }])
    expect(await ben.engine.sync({ pull: true })).toMatchObject({ ok: true, evicted: 0 })
    expect(await ben.db.synced('items').get(note.id)).toMatchObject({ title: 'Back again', access: 'read' })
    expect(ben.notices.filter((n) => n.summary === 'Access removed')).toHaveLength(0)
  })

  it('the live channel answers the server’s ping, and relays editing notices of the others only', async () => {
    const heard: { user: string; to: string }[] = []
    const ana = client(ANA, { engine: { onEditing: (m) => heard.push({ user: m.user, to: 'ana' }) } })
    const ben = client(BEN, { engine: { onEditing: (m) => heard.push({ user: m.user, to: 'ben' }) } })
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben)
    server.ping()
    await until(() => [...server.connections].every((c) => c.pongs === 1), 'the pongs')
    server.startEditing(ANA, note.id, 'description')
    await until(() => heard.length === 1, 'the notice')
    expect(heard).toEqual([{ user: 'Ana Muster', to: 'ben' }])
    server.endEditing(ANA, note.id, 'description')
    await until(() => heard.length === 2, 'the end')
  })

  it('close 4401 ends the session: no reconnect, the app is told', async () => {
    const ben = client(BEN)
    ben.engine.start()
    await live(ben)
    server.close(BEN, 4401)
    await until(() => ben.signedOut === 1, 'signed out')
    expect(ben.engine.status.connection).toBe('signed-out')
  })

  it('a refused change is undone, with the reason in a toast', async () => {
    const ana = client(ANA, { live: false })
    const idea = await ana.repo.create<ItemRow>('items', { type: 'idea', title: 'Not yet' })
    const result = await ana.engine.sync({ pull: true })
    expect(result).toMatchObject({ ok: true, rejected: 1 })
    expect(await ana.db.synced('items').get(idea.id)).toBeUndefined()
    expect(await ana.db.outbox.count()).toBe(0)
    expect(ana.notices).toContainEqual(
      expect.objectContaining({
        severity: 'error',
        summary: 'Change undone',
        detail: expect.stringContaining('Not yet'),
      }),
    )
  })

  it('a lost answer is pushed again under the same mutation id; later changes are not merged into it', async () => {
    let lose = true
    const ana = client(ANA, {
      live: false,
      engine: {
        post: async (body) => {
          const answer = await server.sync(ANA, body)
          if (lose && body.mutations.length) {
            lose = false
            throw new ApiError(0, undefined)
          }
          return answer
        },
      },
    })
    const note = await ana.repo.create<ItemRow>('items', { type: 'note', title: 'Once' })
    expect(await ana.engine.sync({ pull: true })).toMatchObject({ ok: false, offline: true })
    expect((await ana.db.outbox.toArray())[0]!.sentAt).toBeTruthy()
    await ana.repo.patch('items', note.id, { title: 'Once more' })
    expect(await ana.db.outbox.count()).toBe(2)
    expect(await ana.engine.sync({ pull: true })).toMatchObject({ ok: true })
    expect([...server.table('items').values()]).toHaveLength(1)
    expect(serverRow(note.id)).toMatchObject({ title: 'Once more', number: 1 })
    const pushes = server.requests.filter((r) => r.mutations.length)
    expect(pushes[1]!.mutations[0]!.mutationId).toBe(pushes[0]!.mutations[0]!.mutationId)
    expect(await ana.db.outbox.count()).toBe(0)
    expect(await ana.db.synced('items').get(note.id)).toMatchObject({ title: 'Once more', number: 1 })
  })

  it('a duplicate of a unique pair is merged into the existing row (mergedInto)', async () => {
    const laptop = client(ANA, { live: false })
    const phone = client(ANA, { live: false, db: memoryDb() })
    const first = await laptop.repo.create('user_settings', { key: 'list:note', value: { sort: 'title' } })
    await laptop.engine.sync({ pull: true })
    const second = await phone.repo.create('user_settings', { key: 'list:note', value: { sort: '-title' } })
    await phone.engine.sync({ pull: true })
    expect(await phone.db.synced('user_settings').get(second.id)).toBeUndefined()
    // The later device wins: its value overwrote the existing one (docs/03 §15 #36).
    expect(await phone.db.synced('user_settings').get(first.id)).toMatchObject({
      key: 'list:note',
      value: { sort: '-title' },
    })
    expect([...server.table('user_settings').values()]).toHaveLength(1)
    expect(server.table('user_settings').get(first.id)).toMatchObject({ value: { sort: '-title' }, rev: 2 })
  })

  it('pulls page after page until the server has no more', async () => {
    for (let i = 0; i < 7; i++) server.seed('persons', { id: `p${i}`, displayName: `Person ${i}`, isActive: true })
    const ben = client(BEN, { live: false, engine: { pageLimit: 2 } })
    const result = await ben.engine.sync({ pull: true })
    expect(result).toMatchObject({ ok: true, pulled: 10 })
    expect(await ben.db.synced('persons').count()).toBe(8)
    expect(server.requests.length).toBeGreaterThan(3)
    expect(await ben.db.getMeta('cursor', 0)).toBe(server.seq)
  })

  it('without the live channel, a local change goes out with a pull shortly after', async () => {
    const ana = client(ANA, { live: false })
    ana.engine.start()
    await until(() => ana.engine.status.connection === 'online', 'online')
    const note = await ana.repo.create<ItemRow>('items', { type: 'note', title: 'Polled' })
    await until(() => server.table('items').has(note.id), 'the push')
    const push = server.requests.find((r) => r.mutations.length)!
    expect(push.pull).toBe(true)
    await until(async () => (await ana.db.synced('items').get(note.id))?.number === 1, 'the number')
  })

  it('reconnects after the live channel drops, and catches up what it missed', async () => {
    const ana = client(ANA)
    const ben = client(BEN)
    ana.engine.start()
    ben.engine.start()
    await live(ana, ben)
    const note = await sharedNote(ana, ben)
    server.close(BEN, 1006)
    await ana.repo.patch('items', note.id, { title: 'While Ben reconnected' })
    await until(async () => (await ben.db.synced('items').get(note.id))?.title === 'While Ben reconnected', 'caught up')
    await live(ben)
  })
})
