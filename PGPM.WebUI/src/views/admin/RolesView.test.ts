import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import RolesView from './RolesView.vue'

const { syncNow, status, rolesApi } = vi.hoisted(() => ({
  syncNow: vi.fn(),
  status: { connection: 'live', syncing: false },
  rolesApi: { create: vi.fn(), update: vi.fn(), remove: vi.fn() },
}))
vi.mock('@/local/boot', async () => {
  const { reactive: r } = await import('vue')
  return { engine: { syncNow: () => syncNow(), status: r(status) } }
})
vi.mock('@/api/config', () => ({ rolesApi }))

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

describe('Administration → Assignment roles (docs/03 §17)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    for (const fn of [syncNow, ...Object.values(rolesApi)]) fn.mockReset()
    await db.synced('assignment_roles').bulkPut([
      { ...synced, id: 'r2', name: 'Sponsor', description: null, sortOrder: 20, isArchived: false },
      {
        ...synced,
        id: 'r1',
        name: 'Review board',
        description: 'Decides at the gates',
        sortOrder: 10,
        isArchived: false,
      },
    ])
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    )
  })
  afterEach(async () => {
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
    useDatabase(null)
    await db.delete()
  })

  it('lists the roles in order; a taken name shows under the field, then the role is created', async () => {
    const wrapper = mount(RolesView, {
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, createPinia()] as never[],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { template: '<a><slot /></a>' } },
      },
    })
    await until(() => wrapper.findAll('[data-testid^="role-"]').length === 2, 'the roles')
    expect(wrapper.findAll('[data-testid^="role-"] .strong').map((n) => n.text())).toEqual(['Review board', 'Sponsor'])

    rolesApi.create
      .mockRejectedValueOnce(new ApiError(409, { code: 'duplicate_name', errors: { name: ['This name is taken.'] } }))
      .mockResolvedValueOnce({})
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'New role')!
      .trigger('click')
    const dialog = () => document.body.querySelector('[role="dialog"]') as HTMLElement
    await until(() => !!dialog(), 'the dialog')
    const name = dialog().querySelector<HTMLInputElement>('#rl-name')!
    name.value = 'sponsor'
    name.dispatchEvent(new Event('input'))
    const submit = () => [...dialog().querySelectorAll('button')].find((b) => b.textContent?.includes('Create role'))!
    submit().click()
    await until(() => dialog().textContent!.includes('This name is taken.'), 'the server’s message')
    name.value = 'Data owner'
    name.dispatchEvent(new Event('input'))
    submit().click()
    await until(() => rolesApi.create.mock.calls.length === 2, 'the second call')
    expect(rolesApi.create.mock.calls[1]![0]).toEqual({ name: 'Data owner', description: null, sortOrder: 30 })
    await until(() => syncNow.mock.calls.length === 1, 'the sync')
    wrapper.unmount()
  })
})
