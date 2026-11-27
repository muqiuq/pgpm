import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeResponse } from '@/api/auth'
import { useDatabase } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import type { ItemRow } from '@/local/rows'
import { useSession } from '@/stores/session'
import { memoryDb, until } from '@/test/memoryDb'
import ItemView from './ItemView.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', () => ({
  useRoute: () => ({ query: {}, params: {}, fullPath: '/' }),
  useRouter: () => ({ push, replace: vi.fn() }),
}))

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, _local: { serverRev: 1, pending: false } }
const at = '2026-10-01T10:00:00Z'
const status = (id: string, itemType: string, name: string, extra: Record<string, unknown> = {}) => ({
  ...synced,
  id,
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
})
const item = (id: string, type: ItemRow['type'], title: string, extra: Partial<ItemRow> = {}) => ({
  ...synced,
  id,
  type,
  number: 7,
  title,
  description: null,
  color: null,
  imageFileId: null,
  ownerUserId: 'u-ana',
  statusId: null,
  statusChangedAt: at,
  tags: [],
  originItemId: null,
  convertedToItemId: null,
  pinned: false,
  access: 'edit',
  createdAt: at,
  createdBy: 'u-ana',
  updatedAt: at,
  updatedBy: 'u-ana',
  ...extra,
})

describe('an item’s page (docs/01 §4.4)', () => {
  let db: ReturnType<typeof memoryDb>
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    await db
      .synced('statuses')
      .bulkPut([
        status('s-lead-new', 'lead', 'New', { isDefault: true, maxDays: 30 }),
        status('s-lead-won', 'lead', 'Won', { sortOrder: 40, category: 'closed' }),
        status('s-project-draft', 'project', 'Draft', { isDefault: true }),
      ])
    await db.synced('persons').put({ ...synced, id: 'p1', displayName: 'Petra Partner', isActive: true })
    await db.synced('items').bulkPut([
      item('l1', 'lead', 'ACME deal', {
        statusId: 's-lead-new',
        leadOrganisation: 'ACME AG',
        leadValue: 120000,
        leadCurrency: 'CHF',
        leadNextActionDue: '2026-10-15',
      }),
      item('p9', 'project', 'Website relaunch', { number: 9, statusId: 's-project-draft' }),
    ])
    await db.synced('assignments').put({
      ...synced,
      id: 'a1',
      itemId: 'l1',
      milestoneId: null,
      personId: 'p1',
      role: 'Contact',
      sortOrder: 'a0',
    })
    await db.synced('item_links').bulkPut([
      { ...synced, id: 'k1', fromItemId: 'l1', toItemId: 'p9', relation: 'related' },
      { ...synced, id: 'k2', fromItemId: 'l1', toItemId: 'not-here', relation: 'depends_on' },
    ])
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function open(props: { type: ItemRow['type']; id: string; tab?: string }, rights: Record<string, string>) {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSession().set({
      user: { id: 'u-ana', displayName: 'Ana', groupIds: [], isAdmin: false, rights, personId: null },
      flags: { mustChangePassword: false, totpSetupRequired: false },
      session: { totpDue: false },
    } as unknown as MeResponse)
    const wrapper = mount(ItemView, {
      props,
      attachTo: document.body,
      global: {
        plugins: [
          [PrimeVue, { unstyled: true }],
          ToastService,
          ConfirmationService,
          pinia,
          [VueQueryPlugin, { queryClient: new QueryClient() }],
        ],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { props: ['to'], template: '<a><slot /></a>' } },
      },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const button = (name: string) =>
    [...document.body.querySelectorAll('button')].find(
      (b) => b.getAttribute('aria-label') === name || b.textContent?.trim() === name,
    )

  it('shows a lead with its fixed fields, status with overdue, persons, and offers the conversion', async () => {
    const wrapper = open({ type: 'lead', id: 'l1' }, { Leads: 'Create', Projects: 'Create' })
    await until(() => wrapper.text().includes('ACME AG'), 'the lead')
    await until(() => wrapper.text().includes('Petra Partner'), 'the person')
    const text = wrapper.text()
    expect(wrapper.find('h1').text()).toBe('ACME deal')
    expect(text).toContain('L-7')
    expect(text).toContain('New')
    expect(text).toContain('Overdue')
    expect(text).toContain('Organisation')
    expect(text).toContain('CHF')
    expect(text).toContain('Next action due')
    expect(text).toContain('Contact')
    expect(button('Convert to project')).toBeDefined()
    expect(button('Edit description')).toBeDefined()
    expect(button('Edit Organisation')?.disabled).toBe(false)
  })

  it('is read-only with read access: no edit, no conversion', async () => {
    await db.synced('items').put(item('l1', 'lead', 'ACME deal', { statusId: 's-lead-new', access: 'read' }))
    const wrapper = open({ type: 'lead', id: 'l1' }, { Leads: 'Create', Projects: 'Create' })
    await until(() => wrapper.text().includes('Read only'), 'the read-only lead')
    expect(button('Convert to project')).toBeUndefined()
    expect(button('Edit description')).toBeUndefined()
    expect(button('Assign person')).toBeUndefined()
    expect(document.body.querySelector('button[aria-label="Edit Title"]')).toBeNull()
  })

  it('lists links both ways on the Links tab, an unreadable target as restricted', async () => {
    const wrapper = open({ type: 'lead', id: 'l1', tab: 'links' }, { Leads: 'Create' })
    await until(() => wrapper.text().includes('Website relaunch'), 'the links')
    expect(wrapper.text()).toContain('related to')
    expect(wrapper.text()).toContain('depends on')
    expect(wrapper.text()).toContain('Restricted')
    const project = open({ type: 'project', id: 'p9', tab: 'links' }, {})
    await until(() => project.text().includes('ACME deal'), 'the incoming link')
  })

  it('converts the lead into a project on the device and opens it', async () => {
    const wrapper = open({ type: 'lead', id: 'l1' }, { Leads: 'Create', Projects: 'Create' })
    await until(() => !!button('Convert to project'), 'the button')
    button('Convert to project')!.click()
    await until(() => !!button('Convert'), 'the dialog')
    expect(document.body.textContent).toContain('moves to Won')
    button('Convert')!.click()
    await until(() => push.mock.calls.length > 0, 'the project page')
    const target = push.mock.calls[0]![0] as { name: string; params: { id: string } }
    expect(target.name).toBe('project')
    const project = (await db.synced('items').get(target.params.id)) as unknown as ItemRow
    expect(project).toMatchObject({
      type: 'project',
      title: 'ACME deal',
      originItemId: 'l1',
      statusId: 's-project-draft',
    })
    expect((await db.synced('items').get('l1'))?.statusId).toBe('s-lead-won')
    expect(await db.synced('assignments').where('itemId').equals(project.id).count()).toBe(1)
    await flushPromises()
    expect(wrapper.exists()).toBe(true)
  })
})
