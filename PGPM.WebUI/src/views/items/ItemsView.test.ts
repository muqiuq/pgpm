import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeResponse } from '@/api/auth'
import { useDatabase } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import { useSession } from '@/stores/session'
import { memoryDb, until } from '@/test/memoryDb'
import { createRequested } from './createRequest'
import ItemsView from './ItemsView.vue'

const { route, replace } = vi.hoisted(() => ({
  route: { query: {} as Record<string, string>, fullPath: '/notes' },
  replace: vi.fn(),
}))
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push: vi.fn(), replace }) }))

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, _local: { serverRev: 1, pending: false } }
const at = '2026-11-20T10:00:00Z'
const note = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
  ...synced,
  id,
  type: 'note',
  number: Number(id.slice(1)),
  title,
  description: null,
  ownerUserId: 'u-ana',
  statusId: null,
  pinned: false,
  access: 'manage',
  createdAt: at,
  updatedAt: at,
  ...extra,
})

describe('the item list page (docs/01 §4.3)', () => {
  let db: ReturnType<typeof memoryDb>
  const mounted: { unmount(): void }[] = []
  const warnings: string[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    route.query = {}
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    vi.spyOn(console, 'warn').mockImplementation((...args) => void warnings.push(args.join(' ')))
    await db
      .synced('items')
      .bulkPut([
        note('n1', 'Lessons from the pilot'),
        note('n2', 'Shopping list', { pinned: true, updatedAt: '2026-11-01T00:00:00Z' }),
        { ...note('p3', 'Pilot project'), type: 'project', number: 3, statusId: 's' },
      ])
    await db
      .synced('item_links')
      .put({ ...synced, id: 'l1', fromItemId: 'n1', toItemId: 'p3', relation: 'learning_of' })
    await db.synced('saved_views').put({
      ...synced,
      id: 'v-learnings',
      ownerUserId: 'u-boss',
      itemType: 'note',
      name: 'Learnings',
      isShared: true,
      sortOrder: 10,
      definition: {
        search: '',
        filters: { hasLink: { relation: 'learning_of', direction: 'outgoing', targetTypes: ['project', 'idea'] } },
        sort: ['-updated_at'],
        columns: ['key', 'title', 'links', 'owner', 'updated_at'],
      },
    })
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function open() {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSession().set({
      user: {
        id: 'u-ana',
        displayName: 'Ana',
        groupIds: [],
        isAdmin: false,
        rights: { Notes: 'Create' },
        personId: null,
      },
      flags: { mustChangePassword: false, totpSetupRequired: false },
      session: { totpDue: false },
    } as unknown as MeResponse)
    const wrapper = mount(ItemsView, {
      props: { type: 'note' },
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService, pinia],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { props: ['to'], template: '<a><slot /></a>' } },
      },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const rows = () => [...document.body.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '')

  it('lists the notes of the device, pinned first, with New note and the default columns', async () => {
    const wrapper = open()
    await until(() => rows().length === 2, 'the notes')
    expect(rows()[0]).toContain('Shopping list')
    expect(rows()[1]).toContain('Lessons from the pilot')
    expect(wrapper.text()).toContain('New note')
    const headers = [...document.body.querySelectorAll('thead th')].map((h) => h.textContent?.trim())
    // The sorted column carries its position in the multi-column sort ("Modified1").
    expect(headers).toEqual(['', 'Key', 'Title', 'Owner', 'Modified1'])
    expect(warnings.filter((w) => w.includes('[Vue warn]'))).toEqual([])
  })

  it('opens its create dialog when a link asked for it (`?new=1`, the installed app’s New note)', async () => {
    createRequested.value = 'note'
    open()
    await until(() => !!document.body.querySelector('[role="dialog"]'), 'the dialog')
    expect(document.body.querySelector('[role="dialog"]')!.textContent).toContain('New note')
    expect(createRequested.value).toBeNull()
  })

  it('opens a linked saved view: the seeded Learnings shows the notes that are a learning of a project', async () => {
    route.query = { view: 'v-learnings' }
    open()
    await until(() => rows().length === 1 && rows()[0]!.includes('Lessons from the pilot'), 'the learnings')
    expect(rows()[0]).toContain('is learning of')
    const headers = [...document.body.querySelectorAll('thead th')].map((h) => h.textContent?.trim())
    expect(headers).toContain('Links')
    expect(document.body.querySelector('[data-testid="saved-views"]')!.textContent).toContain('Learnings')
  })
})
