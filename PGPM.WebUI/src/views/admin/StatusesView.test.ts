import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { reactive } from 'vue'
import { useDatabase } from '@/local/db'
import { FakeSyncServer } from '@/test/fakeSyncServer'
import { memoryDb, until } from '@/test/memoryDb'
import StatusesView from './StatusesView.vue'

const { syncNow, status } = vi.hoisted(() => ({
  syncNow: vi.fn(),
  status: { connection: 'live', syncing: false },
}))
vi.mock('@/local/boot', async () => {
  const { reactive: r } = await import('vue')
  return { engine: { syncNow: () => syncNow(), status: r(status) } }
})
vi.mock('vue-router', () => ({ useRoute: () => reactive({ query: {} }), useRouter: () => ({ replace: vi.fn() }) }))

const ADMIN = '01970000-0000-7000-8000-00000000c001'
const ids = {
  new: '01970000-0000-7000-8000-000000005001',
  elaborating: '01970000-0000-7000-8000-000000005002',
  assessed: '01970000-0000-7000-8000-000000005003',
}

describe('Administration → Statuses (docs/01 §5)', () => {
  let db: ReturnType<typeof memoryDb>
  let server: FakeSyncServer

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    syncNow.mockReset()
    status.connection = 'live'
    server = new FakeSyncServer()
    server.addUser({ id: ADMIN, name: 'Ada Admin', configManage: true })
    const base = { itemType: 'idea', icon: null, maxDays: null, isArchived: false, isDefault: false }
    const rows = [
      { ...base, id: ids.new, name: 'New', color: '#64748b', category: 'open', sortOrder: 10, isDefault: true },
      { ...base, id: ids.elaborating, name: 'Elaborating', color: '#0ea5e9', category: 'open', sortOrder: 20 },
      { ...base, id: ids.assessed, name: 'Assessed', color: '#6366f1', category: 'open', sortOrder: 30, maxDays: 84 },
    ]
    for (const row of rows) {
      const stored = server.seed('statuses', row)
      await db.synced('statuses').put({ ...stored })
    }
    vi.stubGlobal('fetch', server.fetchFor(ADMIN))
    // jsdom has no matchMedia, which PrimeVue's Dialog and Select use.
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
  })
  afterEach(async () => {
    vi.unstubAllGlobals()
    useDatabase(null)
    await db.delete()
  })

  function view() {
    return mount(StatusesView, {
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, createPinia()],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { template: '<a><slot /></a>' } },
      },
    })
  }

  const names = (wrapper: ReturnType<typeof view>) => wrapper.findAll('li .name').map((n) => n.text())
  const serverOrder = () =>
    [...server.table('statuses').values()]
      .filter((s) => s.itemType === 'idea')
      .sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder))
      .map((s) => s.name)

  it('lists the type’s statuses in order and moves one up through the order endpoint', async () => {
    const wrapper = view()
    await until(() => names(wrapper).length === 3, 'the statuses')
    expect(names(wrapper)).toEqual(['New', 'Elaborating', 'Assessed'])
    expect(wrapper.get(`[data-testid="status-${ids.new}"]`).text()).toContain('Default')
    expect(wrapper.get(`[data-testid="status-${ids.assessed}"]`).text()).toContain('max. 84 days')
    expect(wrapper.get('[aria-label="Move New up"]').attributes('disabled')).toBeDefined()

    await wrapper.get('[aria-label="Move Assessed up"]').trigger('click')
    await until(() => syncNow.mock.calls.length === 1, 'the sync after the call')
    expect(server.calls).toContain('PUT /api/v1/config/statuses/order')
    expect(serverOrder()).toEqual(['New', 'Assessed', 'Elaborating'])
    wrapper.unmount()
  })

  it('creates a status in the dialog; a taken name shows under the field', async () => {
    const wrapper = view()
    await until(() => names(wrapper).length === 3, 'the statuses')
    const newButton = wrapper.findAll('button').find((b) => b.text() === 'New status')!
    await newButton.trigger('click')
    await flushPromises()
    const dialog = () => document.body.querySelector('[role="dialog"]') as HTMLElement
    await until(() => !!dialog(), 'the dialog')
    const submit = () => [...dialog().querySelectorAll('button')].find((b) => b.textContent?.includes('Create status'))!

    submit().click()
    await until(() => dialog().textContent!.includes('A name is needed.'), 'the validation')

    const name = dialog().querySelector<HTMLInputElement>('#st-name')!
    name.value = 'elaborating'
    name.dispatchEvent(new Event('input'))
    submit().click()
    await until(() => dialog().textContent!.includes('This name is taken.'), 'the server’s message')

    name.value = 'Watch'
    name.dispatchEvent(new Event('input'))
    const icon = dialog().querySelector<HTMLInputElement>('#st-icon')!
    icon.value = 'eye'
    icon.dispatchEvent(new Event('input'))
    submit().click()
    await until(() => serverOrder().includes('Watch'), 'the new status on the server')
    const created = [...server.table('statuses').values()].find((s) => s.name === 'Watch')!
    expect(created).toMatchObject({
      itemType: 'idea',
      icon: 'pi-eye',
      category: 'open',
      isDefault: false,
      maxDays: null,
    })
    await until(() => syncNow.mock.calls.length > 0, 'a sync')
    wrapper.unmount()
  })

  it('is read-only offline: the actions are disabled and say why', async () => {
    status.connection = 'offline'
    const wrapper = view()
    await until(() => names(wrapper).length === 3, 'the statuses')
    expect(wrapper.find('[data-testid="offline-hint"]').text()).toContain('Available online only')
    expect(
      wrapper
        .findAll('button')
        .find((b) => b.text() === 'New status')!
        .attributes('disabled'),
    ).toBeDefined()
    expect(wrapper.get('[aria-label="Move Assessed up"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[aria-label="Edit New"]').attributes('disabled')).toBeDefined()
    expect(server.calls).toEqual([])
    wrapper.unmount()
  })
})
