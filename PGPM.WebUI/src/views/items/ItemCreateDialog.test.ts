import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeResponse } from '@/api/auth'
import { engine } from '@/local/boot'
import { useDatabase } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import type { ItemRow } from '@/local/rows'
import { useSession } from '@/stores/session'
import { G_BOARD, G_TEAM, seedConfig, templateId } from '@/test/configFixture'
import { memoryDb, until } from '@/test/memoryDb'
import ItemCreateDialog from './ItemCreateDialog.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }), useRouter: () => ({ push, replace: vi.fn() }) }))

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, _local: { serverRev: 1, pending: false } }

describe('the create dialog (docs/01 §4.4)', () => {
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
    await db.synced('groups').bulkPut([
      { ...synced, id: 'g-team', name: 'Innovation team', description: null },
      { ...synced, id: 'g-board', name: 'Review board', description: null },
    ])
    await db.synced('statuses').bulkPut([
      {
        ...synced,
        id: 's-new',
        itemType: 'idea',
        name: 'New',
        color: '#64748b',
        category: 'open',
        sortOrder: 10,
        isDefault: true,
        isArchived: false,
      },
      {
        ...synced,
        id: 's-old',
        itemType: 'idea',
        name: 'Old',
        color: '#64748b',
        category: 'open',
        sortOrder: 5,
        isDefault: false,
        isArchived: true,
      },
      {
        ...synced,
        id: 's-lead',
        itemType: 'lead',
        name: 'New',
        color: '#64748b',
        category: 'open',
        sortOrder: 10,
        isDefault: true,
        isArchived: false,
      },
    ])
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function open(type: ItemRow['type']) {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSession().set({
      user: { id: 'u-ana', displayName: 'Ana', groupIds: ['g-team'], isAdmin: false, rights: {}, personId: 'p-ana' },
      flags: { mustChangePassword: false, totpSetupRequired: false },
      session: { totpDue: false },
    } as unknown as MeResponse)
    const wrapper = mount(ItemCreateDialog, {
      props: { type, visible: true },
      attachTo: document.body,
      global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, pinia], directives: { tooltip: Tooltip } },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const dialog = () => document.body.querySelector('[role="dialog"]') as HTMLElement
  const button = (label: string) =>
    [...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === label)!

  it('creates an idea with the default status and the creator’s groups at edit, in one batch, then opens it', async () => {
    open('idea')
    await until(() => !!dialog() && dialog().textContent!.includes('Innovation team'), 'the dialog with its groups')
    expect(dialog().getAttribute('aria-labelledby')).toBeTruthy()
    expect(dialog().textContent).toContain('New idea')
    await until(() => dialog().textContent!.includes('New'), 'the default status')
    button('Create idea').click()
    await until(() => dialog().textContent!.includes('A title is needed.'), 'the validation')
    const input = dialog().querySelector<HTMLInputElement>('#new-idea-title')!
    input.value = 'Chatbot for HR'
    input.dispatchEvent(new Event('input'))
    button('Create idea').click()
    await until(() => push.mock.calls.length === 1, 'the navigation')
    const [item] = (await db.synced('items').toArray()) as unknown as ItemRow[]
    expect(item).toMatchObject({ type: 'idea', title: 'Chatbot for HR', statusId: 's-new', ownerUserId: 'u-ana' })
    const entries = await db.outbox.toArray()
    expect(entries.map((e) => [e.table, e.op])).toEqual([
      ['items', 'insert'],
      ['item_shares', 'insert'],
    ])
    expect(entries[1]!.row).toMatchObject({ itemId: item!.id, groupId: 'g-team', access: 'edit' })
    expect(new Set(entries.map((e) => e.batch)).size).toBe(1)
    expect(push).toHaveBeenCalledWith({ name: 'idea', params: { id: item!.id } })
  })

  it('waits for the first sync after signing in: it says so, and Create is off until it is done', async () => {
    engine.status.connection = 'starting'
    try {
      open('idea')
      await until(() => !!dialog() && dialog().textContent!.includes('Preparing your data… (first sync)'), 'the notice')
      expect(button('Create idea').disabled).toBe(true)
      await db.setMeta('lastSyncAt', '2026-11-27T10:00:00Z')
      await until(() => !dialog().textContent!.includes('Preparing your data'), 'the first sync done')
      await until(() => !button('Create idea').disabled, 'Create enabled')
    } finally {
      engine.status.connection = 'stopped'
    }
  })

  it('creates a project from a template offline: its statuses, shares, sections and gates in one batch', async () => {
    await seedConfig(db)
    open('project')
    await until(() => !!dialog() && dialog().textContent!.includes('Blank project'), 'the template picker')
    // Blank: the project statuses, the creator's groups.
    await until(() => dialog().textContent!.includes('Draft'), 'the default status')
    const select = dialog().querySelector('#new-project-template')!.closest('[data-pc-name="select"]') as HTMLElement
    select.click()
    await until(() => !!document.body.querySelector('[role="option"][aria-label="AI use case"]'), 'the options')
    // PrimeVue's Select picks on mousedown.
    document.body
      .querySelector('[role="option"][aria-label="AI use case"]')!
      .dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
    await until(() => dialog().textContent!.includes('Preparing test'), 'the template’s default status')
    await until(() => dialog().textContent!.includes('Review board'), 'the template’s shares')
    const title = dialog().querySelector<HTMLInputElement>('#new-project-title')!
    title.value = 'Invoice recognition'
    title.dispatchEvent(new Event('input'))
    const start = dialog().querySelector<HTMLInputElement>('#new-project-start')!
    start.value = '2026-12-01'
    start.dispatchEvent(new Event('input'))
    button('Create project').click()
    await until(() => push.mock.calls.length === 1, 'the navigation')
    const project = ((await db.synced('items').toArray()) as unknown as ItemRow[]).find((i) => i.type === 'project')!
    expect(project).toMatchObject({
      title: 'Invoice recognition',
      statusId: 's-project-preparing',
      projectTemplateId: templateId,
      projectStartDate: '2026-12-01',
    })
    const entries = await db.outbox.toArray()
    expect(new Set(entries.map((e) => e.batch)).size).toBe(1)
    expect(entries.filter((e) => e.table === 'item_shares').map((e) => e.row!.groupId)).toEqual([G_TEAM, G_BOARD])
    expect(entries.filter((e) => e.table === 'item_sections')).toHaveLength(4)
    expect(entries.filter((e) => e.table === 'milestones').map((e) => e.row!.dueDate)).toEqual([
      '2026-12-01',
      '2026-12-29',
      '2027-01-26',
    ])
  })

  it('creates a note without a status; a project without statuses can’t be created yet', async () => {
    open('project')
    await until(() => !!dialog() && dialog().textContent!.includes('no project statuses'), 'the hint')
    expect(button('Create project').disabled).toBe(true)
    expect(dialog().textContent).toContain('Blank project')
    mounted.splice(0).forEach((w) => w.unmount())

    open('note')
    await until(() => !!dialog() && dialog().textContent!.includes('New note'), 'the note dialog')
    expect(dialog().querySelector('#new-note-status')).toBeNull()
    const input = dialog().querySelector<HTMLInputElement>('#new-note-title')!
    input.value = 'Minutes'
    input.dispatchEvent(new Event('input'))
    button('Create note').click()
    await until(async () => (await db.synced('items').count()) === 1, 'the note')
    expect((await db.synced('items').toArray())[0]).toMatchObject({ type: 'note', statusId: null, title: 'Minutes' })
  })
})
