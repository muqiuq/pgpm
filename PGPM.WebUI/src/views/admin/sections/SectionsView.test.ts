import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase } from '@/local/db'
import type { FieldDefinitionRow, SectionRow } from '@/local/rows'
import { memoryDb, until } from '@/test/memoryDb'
import FieldDialog from './FieldDialog.vue'
import SectionsView from './SectionsView.vue'

const { syncNow, status, api } = vi.hoisted(() => ({
  syncNow: vi.fn(),
  status: { connection: 'live', syncing: false },
  api: {
    sections: {
      create: vi.fn(),
      update: vi.fn(),
      archive: vi.fn(),
      unarchive: vi.fn(),
      order: vi.fn(),
      get: vi.fn(),
    },
    fields: {
      create: vi.fn(),
      update: vi.fn(),
      archive: vi.fn(),
      unarchive: vi.fn(),
      usage: vi.fn(),
      remove: vi.fn(),
      order: vi.fn(),
    },
  },
}))
vi.mock('@/local/boot', async () => {
  const { reactive: r } = await import('vue')
  return { engine: { syncNow: () => syncNow(), status: r(status) } }
})
vi.mock('@/api/config', () => ({ sectionsApi: api.sections, fieldsApi: api.fields }))

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
const id = (n: number) => `01970000-0000-7000-8000-${String(n).padStart(12, '0')}`

function section(n: number, name: string, extra: Partial<SectionRow> = {}): SectionRow {
  return {
    ...synced,
    id: id(n),
    name,
    description: null,
    appliesTo: ['idea', 'project'],
    defaultFor: ['idea'],
    sortOrder: n * 10,
    collapsedByDefault: false,
    isArchived: false,
    canEdit: true,
    ...extra,
  }
}

function field(n: number, sectionId: string, key: string, extra: Partial<FieldDefinitionRow> = {}): FieldDefinitionRow {
  return {
    ...synced,
    id: id(100 + n),
    sectionId,
    key,
    label: key,
    helpText: null,
    type: 'rating',
    config: { min: 0, max: 5, allowNa: true },
    required: false,
    defaultValue: null,
    sortOrder: n * 10,
    isArchived: false,
    ...extra,
  }
}

const profile = section(1, 'Use case profile')
const feasibility = section(2, 'Feasibility')
const board = section(3, 'Review board decision', { appliesTo: ['idea'], canEdit: false })
const fields = [
  field(1, feasibility.id, 'feas_a', { label: 'Data access' }),
  field(2, feasibility.id, 'feas_b', { label: 'Infrastructure' }),
  field(3, feasibility.id, 'feasibility_index', {
    label: 'Feasibility index',
    type: 'score',
    config: { op: 'sum', inputs: ['feas_a', 'feas_b', 'nope'], na: 'rescale', max: 10 },
  }),
  field(4, profile.id, 'problem', { label: 'Addressed problem', type: 'textarea', config: {} }),
]

const plugins = () => [[PrimeVue, { unstyled: true }], ToastService, createPinia()] as never[]
const global = () => ({
  plugins: plugins(),
  directives: { tooltip: Tooltip },
  stubs: { RouterLink: { template: '<a><slot /></a>' } },
})
const dialog = () => document.body.querySelector('[role="dialog"]') as HTMLElement | null
const buttonIn = (root: ParentNode, text: string) =>
  [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement | undefined
/** jsdom has no matchMedia (Dialog, Select) nor ResizeObserver (Textarea’s auto-resize). */
function stubBrowser() {
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
}
function type(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  input.value = value
  input.dispatchEvent(new Event('input'))
}

describe('Administration → Sections & Fields (docs/01 §6)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    syncNow.mockReset()
    for (const group of Object.values(api)) for (const fn of Object.values(group)) fn.mockReset()
    status.connection = 'live'
    await db.synced('sections').bulkPut([board, profile, feasibility])
    await db.synced('field_definitions').bulkPut(fields)
    await db.synced('groups').bulkPut([
      { ...synced, id: id(900), name: 'Review board', description: null },
      { ...synced, id: id(901), name: 'Compliance', description: null },
    ])
    stubBrowser()
  })
  afterEach(async () => {
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
    useDatabase(null)
    await db.delete()
  })

  const names = (wrapper: ReturnType<typeof mount>) =>
    wrapper.findAll('[data-testid^="section-"] .name').map((n) => n.text())

  it('lists the sections in order and the selected one’s fields; creates a section through the endpoint', async () => {
    const wrapper = mount(SectionsView, { attachTo: document.body, global: global() })
    await until(() => names(wrapper).length === 3, 'the sections')
    expect(names(wrapper)).toEqual(['Use case profile', 'Feasibility', 'Review board decision'])
    expect(wrapper.text()).toContain('Fields of Use case profile')
    await wrapper
      .findAll('button.pick')
      .find((b) => b.text().startsWith('Fields of Feasibility'))!
      .trigger('click')
    await until(() => wrapper.findAll('[data-testid^="field-"]').length === 3, 'the fields')
    expect(wrapper.get('[data-testid="field-feasibility_index"]').text()).toContain('Score (computed)')

    api.sections.create.mockResolvedValue({ ...section(4, 'Charter'), access: [] })
    await buttonIn(wrapper.element, 'New section')!.click()
    await until(() => !!dialog(), 'the dialog')
    buttonIn(dialog()!, 'Create section')!.click()
    await until(() => dialog()!.textContent!.includes('A name is needed.'), 'the validation')
    type(dialog()!.querySelector<HTMLInputElement>('#sc-name')!, ' Charter ')
    await flushPromises()
    buttonIn(dialog()!, 'Create section')!.click()
    await until(() => api.sections.create.mock.calls.length === 1, 'the call')
    expect(api.sections.create.mock.calls[0]![0]).toEqual({
      name: 'Charter',
      description: null,
      appliesTo: ['idea', 'project'],
      defaultFor: [],
      collapsedByDefault: false,
      access: [],
    })
    await until(() => syncNow.mock.calls.length > 0, 'the sync')
    wrapper.unmount()
  })

  it('leaves a section’s groups unchanged when they can’t be read', async () => {
    api.sections.get.mockRejectedValue(new TypeError('Failed to fetch'))
    api.sections.update.mockResolvedValue({ ...board, access: [] })
    const wrapper = mount(SectionsView, { attachTo: document.body, global: global() })
    await until(() => names(wrapper).length === 3, 'the sections')
    await wrapper.get('[aria-label="Edit Review board decision"]').trigger('click')
    await until(() => !!dialog()?.textContent?.includes('Leave the groups as they are'), 'the unknown groups')
    buttonIn(dialog()!, 'Save')!.click()
    await until(() => api.sections.update.mock.calls.length === 1, 'the call')
    expect(api.sections.update.mock.calls[0]).toEqual([
      board.id,
      expect.objectContaining({ name: 'Review board decision', appliesTo: ['idea'], access: null }),
    ])
    wrapper.unmount()
  })

  it('shows the groups the server reports and sends them back', async () => {
    api.sections.get.mockResolvedValue({
      ...board,
      access: [{ groupId: id(900), groupName: 'Review board', level: 'edit' }],
    })
    api.sections.update.mockResolvedValue({ ...board, access: [] })
    const wrapper = mount(SectionsView, { attachTo: document.body, global: global() })
    await until(() => names(wrapper).length === 3, 'the sections')
    await wrapper.get('[aria-label="Edit Review board decision"]').trigger('click')
    await until(() => !!dialog()?.textContent?.includes('Only the groups with Edit change it.'), 'the groups')
    buttonIn(dialog()!, 'Save')!.click()
    await until(() => api.sections.update.mock.calls.length === 1, 'the call')
    expect(api.sections.update.mock.calls[0]![1].access).toEqual([{ groupId: id(900), level: 'edit' }])
    wrapper.unmount()
  })

  it('creates a field with a key from its label; deleting one names what goes with it', async () => {
    api.fields.create.mockResolvedValue({})
    api.fields.usage.mockResolvedValue({
      id: fields[3]!.id,
      key: 'problem',
      values: 3,
      items: 2,
      scores: [],
      templates: 1,
    })
    api.fields.remove.mockResolvedValue(undefined)
    const wrapper = mount(SectionsView, { attachTo: document.body, global: global() })
    await until(() => wrapper.findAll('[data-testid^="field-"]').length === 1, 'the profile’s fields')
    await buttonIn(wrapper.element, 'New field')!.click()
    await until(() => !!dialog(), 'the dialog')
    type(dialog()!.querySelector<HTMLInputElement>('#fd-label')!, 'Business unit')
    await flushPromises()
    expect(dialog()!.querySelector<HTMLInputElement>('#fd-key')!.value).toBe('business_unit')
    buttonIn(dialog()!, 'Create field')!.click()
    await until(() => api.fields.create.mock.calls.length === 1, 'the call')
    expect(api.fields.create.mock.calls[0]![0]).toEqual({
      sectionId: profile.id,
      key: 'business_unit',
      type: 'text',
      label: 'Business unit',
      helpText: null,
      config: {},
      required: false,
      defaultValue: null,
    })
    await until(() => !dialog(), 'the dialog closed')

    await wrapper.get('[aria-label="More actions for Addressed problem"]').trigger('click')
    await until(() => !!document.body.querySelector('[role="menuitem"]'), 'the menu')
    const item = [...document.body.querySelectorAll<HTMLElement>('[role="menuitem"]')].find((m) =>
      m.textContent?.includes('Delete'),
    )!
    ;(item.querySelector('a, div') as HTMLElement | null)?.click()
    item.click()
    await until(() => !!document.body.querySelector('[data-testid="field-usage"]'), 'the usage')
    expect(document.body.querySelector('[data-testid="field-usage"]')!.textContent).toContain(
      '3 values on 2 items go with it.',
    )
    buttonIn(dialog()!, 'Delete')!.click()
    await until(() => api.fields.remove.mock.calls.length === 1, 'the delete')
    expect(api.fields.remove.mock.calls[0]).toEqual([fields[3]!.id, 3])
    wrapper.unmount()
  })
})

describe('the score editor in the field dialog (docs/03 §16)', () => {
  beforeEach(() => {
    api.fields.update.mockReset()
    stubBrowser()
  })
  afterEach(() => {
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
  })

  it('refuses an invalid score with the reason per input, and saves it once valid', async () => {
    api.fields.update.mockResolvedValue({})
    const score = fields[2]!
    const wrapper = mount(FieldDialog, {
      attachTo: document.body,
      props: { visible: true, field: score, sectionId: feasibility.id, sections: [feasibility], fields },
      global: global(),
    })
    await until(() => !!dialog(), 'the dialog')
    expect(dialog()!.querySelector('[data-testid="score-preview"]')!.textContent).toContain('Shown as … / 10')
    buttonIn(dialog()!, 'Save')!.click()
    await until(() => dialog()!.textContent!.includes('nope doesn’t exist (any more).'), 'the validation')
    expect(api.fields.update).not.toHaveBeenCalled()

    dialog()!.querySelector<HTMLButtonElement>('[aria-label="Remove input nope"]')!.click()
    await flushPromises()
    expect(dialog()!.querySelector('[data-testid="score-preview"]')!.textContent).toContain('add up to 10')
    buttonIn(dialog()!, 'Save')!.click()
    await until(() => api.fields.update.mock.calls.length === 1, 'the call')
    expect(api.fields.update.mock.calls[0]).toEqual([
      score.id,
      {
        sectionId: feasibility.id,
        label: 'Feasibility index',
        helpText: null,
        required: false,
        defaultValue: null,
        config: {
          op: 'sum',
          inputs: ['feas_a', 'feas_b'],
          na: 'rescale',
          max: 10,
          scale: 1,
          normalize: false,
          decimals: 0,
        },
      },
    ])
    wrapper.unmount()
  })
})
