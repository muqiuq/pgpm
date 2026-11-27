import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase, type PgpmDb } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import { memoryDb, until } from '@/test/memoryDb'
import { button, menuItem, mountPage, type PageUser } from '@/test/mountPage'
import MapsView from './MapsView.vue'

const { route, push } = vi.hoisted(() => ({ route: { query: {} as Record<string, string> }, push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push, replace: vi.fn() }) }))

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, _local: { serverRev: 1, pending: false } }
const at = '2026-11-20T10:00:00Z'
const map = (id: string, name: string, extra: Record<string, unknown> = {}) => ({
  ...synced,
  id,
  kind: 'canvas',
  name,
  description: null,
  ownerUserId: 'u-ana',
  settings: { grid: true },
  matrix: null,
  defaultView: null,
  isTemplate: false,
  templateSourceId: null,
  preview: null,
  access: 'manage',
  createdAt: at,
  updatedAt: at,
  ...extra,
})

const rights = { Maps: 'Create', Configuration: 'Manage' }

describe('the map list (doc 01 §12.1)', () => {
  let db: PgpmDb
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    route.query = {}
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    await db.synced('groups').put({ ...synced, id: 'g1', name: 'Innovation team', description: null })
    await db.synced('maps').bulkPut([
      map('m1', 'Workshop board', { description: 'Kick-off' }),
      map('m2', 'Read-only board', { access: 'read', ownerUserId: 'u-bob' }),
      map('t1', 'Crazy 8s', { isTemplate: true, access: 'read' }),
      map('t2', 'Prioritization matrix', {
        kind: 'matrix',
        isTemplate: true,
        access: 'read',
        matrix: { x: { field: 'feas', min: 0, max: 65 }, y: { field: 'impact', min: 0, max: 100 } },
      }),
    ])
    await db.synced('map_layers').bulkPut([
      { ...synced, id: 'L1', mapId: 'm1', name: 'Base', sortOrder: 'a0', isVisible: true, isLocked: false },
      { ...synced, id: 'T1', mapId: 't1', name: 'Frames', sortOrder: 'a0', isVisible: true, isLocked: false },
      { ...synced, id: 'T2', mapId: 't2', name: 'Base', sortOrder: 'a0', isVisible: true, isLocked: false },
    ])
    await db.synced('map_elements').put({
      ...synced,
      id: 'E1',
      mapId: 't1',
      layerId: 'T1',
      kind: 'rect',
      z: 'a0',
      x: 0,
      y: 0,
      width: 100,
      height: 100,
      style: {},
    })
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function open(user: PageUser = { rights, groupIds: ['g1'] }) {
    const wrapper = mountPage(MapsView, {}, user)
    mounted.push(wrapper)
    return wrapper
  }
  const choose = async (label: string) => {
    await until(() => !!menuItem(label), label)
    const entry = menuItem(label)!
    ;(entry.querySelector<HTMLElement>('[data-pc-section="itemcontent"]') ?? entry).click()
    await flushPromises()
  }
  const actionsOf = async (name: string) => {
    await until(() => !!button(`Actions for ${name}`), `the actions of ${name}`)
    button(`Actions for ${name}`)!.click()
    await flushPromises()
  }
  const created = async () =>
    (await db.synced('maps').toArray()).filter((m) => !['m1', 'm2', 't1', 't2'].includes(m.id))

  it('lists the maps and, on its own tab, the templates', async () => {
    const wrapper = open()
    await until(() => wrapper.text().includes('Workshop board'), 'the maps')
    expect(wrapper.text()).toContain('Kick-off')
    expect(wrapper.text()).toContain('Read only')
    expect(wrapper.text()).not.toContain('Crazy 8s')
    wrapper.unmount()
    route.query = { tab: 'templates' }
    const templates = open()
    await until(() => templates.text().includes('Crazy 8s'), 'the templates')
    expect(templates.text()).toContain('Prioritization matrix')
    expect(templates.text()).not.toContain('Workshop board')
  })

  it('creates a canvas map with its layer and the creator’s groups, and opens it', async () => {
    open()
    await until(() => !!button('New map'), 'the button')
    button('New map')!.click()
    await choose('Canvas map')
    await until(() => !!document.body.querySelector('#new-map-name'), 'the dialog')
    await until(() => document.body.textContent!.includes('Members of these groups'), 'the preselected group')
    const input = document.body.querySelector<HTMLInputElement>('#new-map-name')!
    input.value = 'Strategy day'
    input.dispatchEvent(new Event('input'))
    button('Create map')!.click()
    await until(() => push.mock.calls.length > 0, 'the map page')
    const [made] = await created()
    expect(made).toMatchObject({ kind: 'canvas', name: 'Strategy day' })
    expect(push).toHaveBeenCalledWith({ name: 'map', params: { id: made!.id } })
    expect((await db.synced('map_layers').where('mapId').equals(made!.id).toArray()).map((l) => l.name)).toEqual([
      'Base',
    ])
    const shares = await db.synced('map_shares').where('mapId').equals(made!.id).toArray()
    expect(shares.map((s) => [s.groupId, s.access])).toEqual([['g1', 'edit']])
  })

  it('creates a matrix map from the Prioritization matrix preset', async () => {
    open()
    await until(() => !!button('New map'), 'the button')
    button('New map')!.click()
    await choose('Matrix map')
    await until(() => document.body.textContent!.includes('Preset'), 'the dialog')
    const input = document.body.querySelector<HTMLInputElement>('#new-map-name')!
    input.value = 'Review board'
    input.dispatchEvent(new Event('input'))
    button('Create map')!.click()
    await until(() => push.mock.calls.length > 0, 'the map page')
    const [made] = await created()
    expect(made).toMatchObject({ kind: 'matrix', name: 'Review board', templateSourceId: 't2', isTemplate: false })
    expect(made!.matrix).toMatchObject({ x: { field: 'feas' } })
  })

  it('duplicates a map, marks one as a template, and deletes one to the Trash', async () => {
    const wrapper = open()
    await actionsOf('Workshop board')
    await choose('Duplicate…')
    await until(() => !!button('Duplicate'), 'the dialog')
    expect(document.body.querySelector<HTMLInputElement>('#new-map-name')!.value).toBe('Copy of Workshop board')
    button('Duplicate')!.click()
    await until(() => push.mock.calls.length > 0, 'the copy')
    const [copy] = await created()
    expect(copy).toMatchObject({ name: 'Copy of Workshop board', isTemplate: false })
    expect(await db.synced('map_layers').where('mapId').equals(copy!.id).count()).toBe(1)

    await actionsOf('Workshop board')
    await choose('Mark as template')
    await until(async () => (await db.synced('maps').get('m1'))?.isTemplate === true, 'the template')

    await until(() => wrapper.text().includes('Read-only board'), 'the list')
    await actionsOf('Read-only board')
    // Read access: no rename, no template, no delete.
    expect(menuItem('Rename…')).toBeUndefined()
    expect(menuItem('Delete…')).toBeUndefined()
    expect(menuItem('Share…')).toBeDefined()

    await actionsOf(copy!.name as string)
    await choose('Delete…')
    await until(() => !!button('Delete'), 'the confirmation')
    button('Delete')!.click()
    await until(async () => {
      const row = await db.synced('maps').get(copy!.id)
      return !row || row.deleted === true
    }, 'the deletion')
  })

  it('offers no New map without Maps: Create', async () => {
    const wrapper = open({ rights: {}, groupIds: [] })
    await until(() => wrapper.text().includes('Workshop board'), 'the maps')
    expect(button('New map')).toBeUndefined()
  })
})
