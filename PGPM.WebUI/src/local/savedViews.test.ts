import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { PgpmDb } from './db'
import { Repo } from './repo'
import type { SavedViewRow } from './rows'
import {
  canChange,
  createView,
  deleteView,
  renameView,
  sameDefinition,
  updateView,
  viewsFor,
  type ViewUser,
} from './savedViews'

const ana: ViewUser = { userId: 'u-ana', configManage: false }
const admin: ViewUser = { userId: 'u-boss', configManage: true }

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
const view = (id: string, extra: Partial<SavedViewRow>): SavedViewRow => ({
  ...synced,
  id,
  ownerUserId: 'u-ana',
  itemType: 'note',
  name: id,
  definition: {},
  isShared: false,
  sortOrder: 10,
  ...extra,
})

describe('saved views (docs/01 §4.3, data model §7.8)', () => {
  let db: PgpmDb
  let repo: Repo
  beforeEach(() => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => 'u-ana' })
  })
  afterEach(() => db.delete())

  it('lists own and shared views of the type, shared first', () => {
    const rows = [
      view('mine', { sortOrder: 5 }),
      view('learnings', { isShared: true, ownerUserId: 'u-boss' }),
      view('bens', { ownerUserId: 'u-ben' }),
      view('idea', { itemType: 'idea' }),
      view('gone', { deleted: true }),
    ]
    expect(viewsFor(rows, 'note', 'u-ana').map((v) => v.id)).toEqual(['learnings', 'mine'])
  })

  it('lets owners change their views; shared ones need Configuration: Manage', () => {
    expect(canChange(view('a', {}), ana)).toBe(true)
    expect(canChange(view('a', { isShared: true }), ana)).toBe(false)
    expect(canChange(view('a', { isShared: true, ownerUserId: 'u-boss' }), admin)).toBe(true)
    expect(canChange(view('a', { ownerUserId: 'u-boss' }), ana)).toBe(false)
  })

  it('creates, updates, renames and deletes through the repository (outbox), shared only with the right', async () => {
    await expect(
      createView(repo, { type: 'note', name: 'Everyone', state: {}, shared: true }, ana, []),
    ).rejects.toThrow(/Configuration/)
    await expect(createView(repo, { type: 'note', name: '  ', state: {}, shared: false }, ana, [])).rejects.toThrow(
      /name/,
    )
    const created = await createView(
      repo,
      {
        type: 'note',
        name: ' Pinned ',
        state: { search: ' x ', filters: { pinned: true }, sort: ['-updated_at'] },
        shared: false,
      },
      ana,
      [view('old', { sortOrder: 30 })],
    )
    expect(created).toMatchObject({
      name: 'Pinned',
      itemType: 'note',
      isShared: false,
      sortOrder: 40,
      ownerUserId: 'u-ana',
      definition: { search: 'x', filters: { pinned: true }, sort: ['-updated_at'], columns: [] },
    })
    const row = (await db.synced('saved_views').get(created.id)) as unknown as SavedViewRow
    await updateView(repo, row, { search: '', filters: {}, sort: ['title'], columns: ['key', 'title'] }, ana)
    await renameView(repo, (await db.synced('saved_views').get(created.id)) as unknown as SavedViewRow, 'Renamed', ana)
    const changed = (await db.synced('saved_views').get(created.id)) as unknown as SavedViewRow
    expect(changed).toMatchObject({ name: 'Renamed', definition: { sort: ['title'], columns: ['key', 'title'] } })
    // A never-synced insert with patches is one entry.
    expect(await db.outbox.count()).toBe(1)
    await deleteView(repo, changed, ana)
    expect(await db.synced('saved_views').get(created.id)).toBeUndefined()
    expect(await db.outbox.count()).toBe(0)
    await expect(deleteView(repo, view('shared', { isShared: true }), ana)).rejects.toThrow(/owner/)
  })

  it('compares definitions: the same state is not modified; columns only if the view has them', () => {
    const saved = { search: '', filters: { hasLink: { relation: 'learning_of' } }, sort: ['-updated_at'], columns: [] }
    expect(
      sameDefinition(saved, {
        search: ' ',
        filters: { hasLink: { relation: 'learning_of' } },
        sort: ['-updated_at'],
        columns: ['key'],
      }),
    ).toBe(true)
    expect(sameDefinition(saved, { ...saved, sort: ['title'] })).toBe(false)
    expect(sameDefinition({ ...saved, columns: ['key', 'title'] }, { ...saved, columns: ['key'] })).toBe(false)
  })
})
