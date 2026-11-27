import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import type { PackageDiff } from '@/api/config'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import PackagesView from './PackagesView.vue'

const { syncNow, status, packagesApi } = vi.hoisted(() => ({
  syncNow: vi.fn(),
  status: { connection: 'live', syncing: false },
  packagesApi: { list: vi.fn(), dryRun: vi.fn(), apply: vi.fn(), export: vi.fn() },
}))
vi.mock('@/local/boot', async () => {
  const { reactive: r } = await import('vue')
  return { engine: { syncNow: () => syncNow(), status: r(status) } }
})
vi.mock('@/api/config', () => ({ packagesApi }))

const packages = [
  {
    name: 'core',
    title: 'PGPM core',
    description: 'Statuses, groups, the general project.',
    version: '1.0.0',
    builtIn: true,
    imported: { version: '1.0.0', importedAt: '2026-11-27T08:00:00Z', importedBy: null, importedByName: 'Ada Admin' },
  },
  {
    name: 'innovation-ai',
    title: 'AI use cases',
    description: null,
    version: '1.0.0',
    builtIn: true,
    imported: null,
  },
]

const entry = (key: string, entity: string, action: string, changed: string[] = []) => ({
  key,
  entity,
  name: key.split(':')[1]!,
  action,
  adopted: false,
  changed,
})
const diff = (overwritten = false, needsAdmin = false): PackageDiff => ({
  name: 'innovation-ai',
  version: '1.0.0',
  entries: [
    entry('field:feasibility_index', 'field', overwritten ? 'update' : 'conflict', ['label']),
    entry('field:problem', 'field', 'create'),
    entry('status:idea.assessed', 'status', 'unchanged'),
    entry('view:review_board_agenda', 'view', 'create'),
  ],
  counts: { create: 2, update: overwritten ? 1 : 0, unchanged: 1, conflict: overwritten ? 0 : 1 },
  needsAdmin,
  isAdmin: false,
})

const global = () => ({
  plugins: [[PrimeVue, { unstyled: true }], ToastService, createPinia()] as never[],
  directives: { tooltip: Tooltip },
  stubs: { RouterLink: { template: '<a><slot /></a>' } },
})
const button = (wrapper: ReturnType<typeof mount>, text: string) =>
  wrapper.findAll('button').find((b) => b.text().trim() === text)

describe('Administration → Configuration packages (docs/03 §17)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(() => {
    db = memoryDb()
    useDatabase(db)
    for (const fn of [syncNow, ...Object.values(packagesApi)]) fn.mockReset()
    status.connection = 'live'
    packagesApi.list.mockResolvedValue(packages)
  })
  afterEach(async () => {
    useDatabase(null)
    await db.delete()
  })

  it('lists the built-in packages, dry-runs one, overwrites the chosen conflict and applies it', async () => {
    packagesApi.dryRun.mockImplementation(async (_source: unknown, overwrite: string[]) => diff(overwrite.length > 0))
    packagesApi.apply.mockResolvedValue({ diff: diff(true), created: 2, updated: 1, skipped: 0 })
    const wrapper = mount(PackagesView, { attachTo: document.body, global: global() })
    await until(() => wrapper.find('[data-testid="package-core"]').exists(), 'the packages')
    expect(wrapper.get('[data-testid="package-core"]').text()).toContain('Imported 1.0.0')
    expect(wrapper.get('[data-testid="package-core"]').text()).toContain('by Ada Admin')
    expect(wrapper.get('[data-testid="package-innovation-ai"]').text()).toContain('Not imported')

    await wrapper.get('[aria-label="Dry run of AI use cases"]').trigger('click')
    await until(() => wrapper.find('[data-testid="diff-field"]').exists(), 'the diff')
    expect(packagesApi.dryRun).toHaveBeenCalledWith({ builtIn: 'innovation-ai' }, [])
    expect(wrapper.get('[data-testid="diff-counts"]').text()).toContain('1 changed here')
    // Unchanged entries are hidden; the fields' conflict comes first.
    expect(wrapper.find('[data-testid="diff-status"]').exists()).toBe(false)
    const fieldKeys = wrapper.findAll('[data-testid="diff-field"] li').map((li) => li.attributes('data-testid'))
    expect(fieldKeys).toEqual(['entry-field:feasibility_index', 'entry-field:problem'])
    expect(wrapper.get('[data-testid="entry-field:feasibility_index"]').text()).toContain('Changed here')
    expect(wrapper.get('[data-testid="apply-summary"]').text()).toBe('2 new, 0 updated, 1 kept (changed here)')

    await wrapper.get('[id="pd-ow-field:feasibility_index"]').trigger('change')
    await flushPromises()
    expect(wrapper.get('[data-testid="apply-summary"]').text()).toBe('2 new, 1 updated')
    await button(wrapper, 'Dry run again')!.trigger('click')
    await until(() => packagesApi.dryRun.mock.calls.length === 2, 'the second dry run')
    expect(packagesApi.dryRun.mock.calls[1]).toEqual([{ builtIn: 'innovation-ai' }, ['field:feasibility_index']])
    await flushPromises()
    expect(wrapper.get('[data-testid="entry-field:feasibility_index"]').text()).toContain('Update')

    await button(wrapper, 'Apply')!.trigger('click')
    await until(() => packagesApi.apply.mock.calls.length === 1, 'the apply')
    expect(packagesApi.apply).toHaveBeenCalledWith({ builtIn: 'innovation-ai' }, ['field:feasibility_index'])
    await until(() => syncNow.mock.calls.length === 1, 'the sync')
    await until(() => !wrapper.find('[data-testid="dry-run"]').exists(), 'the dry run closed')
    expect(packagesApi.list).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('needs an admin for a package with groups, and lists an invalid upload’s problems', async () => {
    packagesApi.dryRun.mockResolvedValueOnce(diff(false, true))
    const wrapper = mount(PackagesView, { attachTo: document.body, global: global() })
    await until(() => wrapper.find('[data-testid="package-core"]').exists(), 'the packages')
    await wrapper.get('[aria-label="Dry run of AI use cases"]').trigger('click')
    await until(() => wrapper.find('[data-testid="admin-needed"]').exists(), 'the admin hint')
    expect(button(wrapper, 'Apply')!.attributes('disabled')).toBeDefined()

    packagesApi.dryRun.mockRejectedValueOnce(
      new ApiError(400, { code: 'invalid_package', problems: ['fields.field:x.type: unknown type'] }),
    )
    const input = wrapper.get<HTMLInputElement>('[data-testid="package-file"]')
    const file = new File(
      [
        JSON.stringify({
          format: 'pgpm-config-package',
          formatVersion: 1,
          name: 'mine',
          title: 'Mine',
          version: '0.1.0',
        }),
      ],
      'mine.pgpm.json',
      { type: 'application/json' },
    )
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await until(() => wrapper.find('[data-testid="package-problems"]').exists(), 'the problems')
    expect(packagesApi.dryRun.mock.calls[1]![0]).toEqual({ package: expect.objectContaining({ name: 'mine' }) })
    expect(wrapper.get('[data-testid="package-problems"]').text()).toContain('unknown type')
    expect(wrapper.text()).toContain('Dry run · Mine 0.1.0')

    Object.defineProperty(input.element, 'files', {
      value: [new File(['not json'], 'broken.json')],
      configurable: true,
    })
    await input.trigger('change')
    await until(() => wrapper.text().includes('broken.json: The file is not JSON.'), 'the upload error')
    wrapper.unmount()
  })

  it('is online only', async () => {
    status.connection = 'offline'
    const wrapper = mount(PackagesView, { attachTo: document.body, global: global() })
    await flushPromises()
    expect(wrapper.find('[data-testid="offline-hint"]').text()).toContain('Available online only')
    expect(packagesApi.list).not.toHaveBeenCalled()
    expect(button(wrapper, 'Upload package…')!.attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
})
