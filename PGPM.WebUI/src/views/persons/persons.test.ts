import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Component } from 'vue'
import { useDatabase } from '@/local/db'
import type { PersonRow } from '@/local/rows'
import { memoryDb, until } from '@/test/memoryDb'
import PersonDialog from './PersonDialog.vue'
import PersonsView from './PersonsView.vue'
import PersonView from './PersonView.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', async () => {
  const { reactive } = await import('vue')
  const route = reactive({ query: {}, fullPath: '/persons' })
  return { useRoute: () => route, useRouter: () => ({ push, replace: vi.fn() }) }
})

const person = (id: string, extra: Partial<PersonRow> = {}) => ({
  id,
  source: 'manual',
  displayName: id,
  email: null,
  givenName: null,
  surname: null,
  jobTitle: null,
  department: null,
  companyName: null,
  phone: null,
  mobilePhone: null,
  userType: null,
  isActive: true,
  notes: null,
  userId: null,
  deleted: false,
  rev: 1,
  seq: 1,
  ...extra,
})

describe('the persons directory (docs/01 §16)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(() => {
    db = memoryDb()
    useDatabase(db)
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    // Textarea's auto-resize.
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
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    useDatabase(null)
    await db.delete()
  })

  const mounted: { unmount(): void }[] = []
  function mountUi(component: Component, props: Record<string, unknown> = {}) {
    const wrapper = mount(component, {
      props,
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, createPinia()],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' } },
      },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const dialog = () => document.body.querySelector('[role="dialog"]') as HTMLElement
  const button = (label: string) =>
    [...dialog().querySelectorAll('button')].find((b) => b.textContent?.includes(label))!
  function type(id: string, value: string) {
    const input = dialog().querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!
    input.value = value
    input.dispatchEvent(new Event('input'))
  }

  it('creates a manual person offline: a name is needed, the e-mail lower-cased, one insert in the outbox', async () => {
    const wrapper = mountUi(PersonDialog, { person: null, visible: true })
    await until(() => !!dialog(), 'the dialog')
    button('Create person').click()
    await until(() => dialog().textContent!.includes('A name is needed.'), 'the validation')
    type('person-displayName', '  Eva Extern ')
    type('person-email', 'Eva@Partner.EXAMPLE')
    type('person-companyName', 'Partner AG')
    button('Create person').click()
    await until(async () => (await db.synced('persons').count()) === 1, 'the person')
    const [row] = (await db.synced('persons').toArray()) as unknown as PersonRow[]
    expect(row).toMatchObject({
      source: 'manual',
      isActive: true,
      displayName: 'Eva Extern',
      email: 'eva@partner.example',
      companyName: 'Partner AG',
      jobTitle: null,
    })
    expect((await db.outbox.toArray()).map((e) => [e.table, e.op])).toEqual([['persons', 'insert']])
    expect(wrapper.emitted('saved')![0]).toEqual([row!.id])
  })

  it('edits only the notes of a Microsoft person; its directory fields are read-only', async () => {
    const entra = person('p-ms', { source: 'microsoft', displayName: 'Mia Directory', jobTitle: 'CTO' })
    await db.synced('persons').put({ ...entra, _local: { serverRev: 1, pending: false } })
    mountUi(PersonDialog, { person: entra, visible: true })
    await until(() => !!dialog(), 'the dialog')
    expect(dialog().textContent).toContain('From Microsoft Entra')
    expect(dialog().querySelector<HTMLInputElement>('#person-displayName')!.disabled).toBe(true)
    expect(dialog().querySelector<HTMLInputElement>('#person-jobTitle')!.disabled).toBe(true)
    type('person-notes', 'Prefers e-mail.')
    button('Save').click()
    await until(async () => (await db.outbox.count()) === 1, 'the patch')
    const [entry] = await db.outbox.toArray()
    expect(entry).toMatchObject({ table: 'persons', op: 'patch' })
    expect(Object.keys(entry!.changes!)).toEqual(['notes'])
  })

  it('keeps name and e-mail of a user’s own person: they are the account’s', async () => {
    const own = person('p-user', { displayName: 'Ulla User', email: 'ulla@example.org', userId: 'u1' })
    await db.synced('persons').put({ ...own, _local: { serverRev: 1, pending: false } })
    mountUi(PersonDialog, { person: own, visible: true })
    await until(() => !!dialog(), 'the dialog')
    expect(dialog().querySelector<HTMLInputElement>('#person-displayName')!.disabled).toBe(true)
    expect(dialog().querySelector<HTMLInputElement>('#person-email')!.disabled).toBe(true)
    type('person-jobTitle', 'Engineer')
    button('Save').click()
    await until(async () => (await db.outbox.count()) === 1, 'the patch')
    expect(Object.keys((await db.outbox.toArray())[0]!.changes!)).toEqual(['jobTitle'])
  })

  it('lists active persons by default, searches and shows the inactive on request', async () => {
    await db
      .synced('persons')
      .bulkPut([
        person('p1', { displayName: 'Ana Muster', email: 'ana@example.org', department: 'IT' }),
        person('p2', { displayName: 'Ben Gast', userType: 'Guest', source: 'microsoft' }),
        person('p3', { displayName: 'Old Timer', isActive: false }),
      ])
    const wrapper = mountUi(PersonsView)
    const names = () => wrapper.findAll('tbody tr').map((r) => r.text())
    await until(() => names().length === 2, 'the active persons')
    expect(names().join(' ')).not.toContain('Old Timer')
    expect(names().find((n) => n.includes('Ben Gast'))).toContain('Guest')

    await wrapper.get('input[aria-label="Search persons…"]').setValue('example.org')
    await until(() => names().length === 1, 'the search', 2000)
    expect(names()[0]).toContain('Ana Muster')

    await wrapper.get('input[aria-label="Search persons…"]').setValue('')
    await until(() => names().length === 2, 'all again', 2000)
    const toggle = wrapper.findAll('button').find((b) => b.text().includes('With inactive'))!
    await toggle.trigger('click')
    await until(() => names().length === 3, 'with the inactive')
    expect(names().find((n) => n.includes('Old Timer'))).toContain('Inactive')
  })

  it('shows every assignment of a person; an item not on the device is restricted', async () => {
    await db.synced('persons').put(person('p1', { displayName: 'Ana Muster', userId: 'u1', email: 'ana@example.org' }))
    await db.synced('items').bulkPut([
      { id: 'i1', type: 'idea', number: 42, title: 'Chatbot', deleted: false },
      { id: 'pr', type: 'project', number: 7, title: 'Relaunch', deleted: false },
    ])
    await db.synced('milestones').put({ id: 'm1', itemId: 'pr', title: 'Gate 1', deleted: false })
    await db.synced('assignments').bulkPut([
      { id: 'a1', itemId: 'i1', milestoneId: null, personId: 'p1', role: 'Responsible', deleted: false },
      { id: 'a2', itemId: 'gone', milestoneId: null, personId: 'p1', role: null, deleted: false },
      { id: 'a3', itemId: null, milestoneId: 'm1', personId: 'p1', role: 'Review board', deleted: false },
      { id: 'a4', itemId: 'i1', milestoneId: null, personId: 'p2', role: null, deleted: false },
      { id: 'a5', itemId: 'pr', milestoneId: null, personId: 'p1', role: null, deleted: true },
    ])
    const wrapper = mountUi(PersonView, { id: 'p1' })
    await until(() => wrapper.findAll('.assignments li').length === 3, 'the assignments')
    const lines = wrapper.findAll('.assignments li').map((l) => l.text())
    expect(wrapper.text()).toContain('All assignments (3)')
    expect(lines.find((l) => l.includes('I-42'))).toContain('Responsible')
    expect(lines.find((l) => l.includes('P-0007'))).toContain('Gate 1')
    expect(lines.some((l) => l.includes('Restricted'))).toBe(true)
    expect(wrapper.text()).toContain('Linked to a user account')
    expect(wrapper.find('a[href="mailto:ana@example.org"]').exists()).toBe(true)
    await flushPromises()
  })
})
