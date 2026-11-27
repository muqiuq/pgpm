import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import TemplateEditView from './TemplateEditView.vue'

const { syncNow, status, templatesApi, router } = vi.hoisted(() => ({
  syncNow: vi.fn(),
  status: { connection: 'live', syncing: false },
  templatesApi: { create: vi.fn(), update: vi.fn(), archive: vi.fn(), unarchive: vi.fn(), order: vi.fn() },
  router: { push: vi.fn(), replace: vi.fn() },
}))
vi.mock('@/local/boot', async () => {
  const { reactive: r } = await import('vue')
  return { engine: { syncNow: () => syncNow(), status: r(status) } }
})
vi.mock('@/api/config', () => ({ templatesApi }))
vi.mock('vue-router', () => ({ useRouter: () => router, useRoute: () => ({ query: {} }) }))

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
const statusRow = (id: string, name: string, sortOrder: number) => ({
  ...synced,
  id,
  itemType: 'project',
  name,
  color: '#64748b',
  icon: null,
  category: 'active',
  sortOrder,
  isDefault: false,
  maxDays: null,
  isArchived: false,
})
const section = (id: string, name: string, sortOrder: number) => ({
  ...synced,
  id,
  name,
  description: null,
  appliesTo: ['idea', 'project'],
  defaultFor: [],
  sortOrder,
  collapsedByDefault: false,
  isArchived: false,
  canEdit: true,
})

const global = () => ({
  plugins: [[PrimeVue, { unstyled: true }], ToastService, createPinia()] as never[],
  directives: { tooltip: Tooltip },
  stubs: { RouterLink: { template: '<a><slot /></a>' } },
})
const buttonIn = (root: ParentNode, text: string) =>
  [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement | undefined
function type(input: HTMLInputElement, value: string) {
  input.value = value
  input.dispatchEvent(new Event('input'))
}

describe('the project template editor (docs/01 §10.2)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    for (const fn of [syncNow, ...Object.values(templatesApi), ...Object.values(router)]) fn.mockReset()
    await db
      .synced('statuses')
      .bulkPut([statusRow('s-prep', 'Preparing test', 10), statusRow('s-poc', 'Prototype / PoC', 20)])
    await db
      .synced('sections')
      .bulkPut([section('sec-profile', 'Use case profile', 10), section('sec-charter', 'Charter', 20)])
    await db.synced('field_definitions').bulkPut([
      {
        ...synced,
        id: 'f-timebox',
        sectionId: 'sec-charter',
        key: 'timebox_end',
        label: 'End of timebox',
        helpText: null,
        type: 'date',
        config: { timebox: true },
        required: false,
        defaultValue: null,
        sortOrder: 10,
        isArchived: false,
      },
    ])
    await db.synced('project_templates').put({
      ...synced,
      id: 't1',
      name: 'AI use case',
      description: null,
      color: '#8b5cf6',
      icon: 'pi-microchip-ai',
      defaultStatusId: 's-prep',
      defaultShares: [],
      sortOrder: 10,
      isArchived: false,
    })
    await db.synced('template_statuses').bulkPut([
      { ...synced, id: 'ts1', templateId: 't1', statusId: 's-prep' },
      { ...synced, id: 'ts2', templateId: 't1', statusId: 's-poc' },
    ])
    await db.synced('template_sections').bulkPut([
      { ...synced, id: 'tsec1', templateId: 't1', sectionId: 'sec-profile', sortOrder: 10 },
      { ...synced, id: 'tsec2', templateId: 't1', sectionId: 'sec-charter', sortOrder: 20 },
    ])
    await db
      .synced('template_field_defaults')
      .put({ ...synced, id: 'td1', templateId: 't1', fieldId: 'f-timebox', value: '2027-01-31' })
    await db.synced('template_milestones').put({
      ...synced,
      id: 'g0',
      templateId: 't1',
      kind: 'gate',
      title: 'Ready for test',
      description: null,
      offsetDays: 0,
      dod: ['Charter written'],
      onGoStatusId: 's-poc',
      onNoGoStatusId: null,
      sortOrder: 'a0',
    })
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

  it('edits a synced template and sends the complete request – the milestone keeps its id', async () => {
    templatesApi.update.mockResolvedValue({ id: 't1' })
    const wrapper = mount(TemplateEditView, { attachTo: document.body, props: { id: 't1' }, global: global() })
    await until(() => wrapper.find<HTMLInputElement>('#tp-name').exists(), 'the form')
    expect(wrapper.get<HTMLInputElement>('#tp-name').element.value).toBe('AI use case')
    expect(wrapper.text()).toContain('At the start')

    type(wrapper.get<HTMLInputElement>('#tp-name').element, 'AI use case (v2)')
    const milestone = wrapper.get('[data-testid="milestone-0"]')
    buttonIn(milestone.element, 'Add criterion')!.click()
    await flushPromises()
    type(milestone.get<HTMLInputElement>('[aria-label="Criterion 2 of milestone 1"]').element, ' Test data ready ')
    // Removing the section takes its field's default with it.
    await wrapper.get('[aria-label="Remove Charter"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="tp-default-f-timebox"]').exists()).toBe(false)

    buttonIn(wrapper.element, 'Save')!.click()
    await until(() => templatesApi.update.mock.calls.length === 1, 'the call')
    expect(templatesApi.update.mock.calls[0]).toEqual([
      't1',
      {
        name: 'AI use case (v2)',
        description: null,
        color: '#8b5cf6',
        icon: 'pi-microchip-ai',
        defaultStatusId: 's-prep',
        statusIds: ['s-prep', 's-poc'],
        sectionIds: ['sec-profile'],
        fieldDefaults: [],
        defaultShares: [],
        milestones: [
          {
            id: 'g0',
            kind: 'gate',
            title: 'Ready for test',
            description: null,
            offsetDays: 0,
            dod: ['Charter written', 'Test data ready'],
            onGoStatusId: 's-poc',
            onNoGoStatusId: null,
          },
        ],
      },
    ])
    await until(() => syncNow.mock.calls.length === 1, 'the sync')
    wrapper.unmount()
  })

  it('creates a template: a name first, then the page of the new one', async () => {
    templatesApi.create.mockResolvedValue({ id: 't2' })
    const wrapper = mount(TemplateEditView, { attachTo: document.body, props: {}, global: global() })
    await until(() => wrapper.find('#tp-name').exists(), 'the form')
    buttonIn(wrapper.element, 'Create template')!.click()
    await until(() => wrapper.text().includes('A name is needed.'), 'the validation')
    expect(templatesApi.create).not.toHaveBeenCalled()

    type(wrapper.get<HTMLInputElement>('#tp-name').element, 'Workshop')
    buttonIn(wrapper.element, 'Add gate')!.click()
    await flushPromises()
    type(wrapper.get<HTMLInputElement>('#ms-title-0').element, 'Go / no-go')
    buttonIn(wrapper.element, 'Create template')!.click()
    await until(() => templatesApi.create.mock.calls.length === 1, 'the call')
    expect(templatesApi.create.mock.calls[0]![0]).toMatchObject({
      name: 'Workshop',
      statusIds: [],
      sectionIds: [],
      milestones: [{ kind: 'gate', title: 'Go / no-go', offsetDays: 0, dod: [] }],
    })
    expect(templatesApi.create.mock.calls[0]![0].milestones[0]).not.toHaveProperty('id')
    await until(() => router.replace.mock.calls.length === 1, 'the new page')
    expect(router.replace).toHaveBeenCalledWith({ name: 'template', params: { id: 't2' } })
    wrapper.unmount()
  })
})
