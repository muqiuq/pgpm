import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import Checkbox from 'primevue/checkbox'
import ConfirmDialog from 'primevue/confirmdialog'
import ConfirmationService from 'primevue/confirmationservice'
import Select from 'primevue/select'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useDatabase } from '@/local/db'
import { createItem } from '@/local/items'
import { createMilestone, dodItemsOf } from '@/local/milestones'
import { repo, setRepoContext } from '@/local/repo'
import type { ItemRow, MilestoneRow } from '@/local/rows'
import { memoryDb, until } from '@/test/memoryDb'
import ItemMilestones from './ItemMilestones.vue'

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }), useRoute: () => ({ query: {} }) }))
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

const ANA = '01970000-0000-7000-8000-00000000a001'

describe('the Milestones tab (docs/01 §11)', () => {
  let db: ReturnType<typeof memoryDb>
  let project: ItemRow
  let gate: MilestoneRow

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: ANA, changed: null })
    project = await repo.transaction((tx) =>
      createItem(tx, { type: 'project', title: 'Chat bot', statusId: 's-poc', shares: [] }),
    )
    await createMilestone(repo, project.id, { kind: 'milestone', title: 'Prototype built', dueDate: '2020-01-10' })
    gate = await createMilestone(repo, project.id, {
      kind: 'gate',
      title: 'PoC decision',
      dueDate: '2099-03-01',
      dod: ['Result documented', 'Make or buy decided'],
    })
  })
  afterEach(async () => {
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function view(canEdit = true) {
    const Host = defineComponent({
      setup: () => () => [h(ConfirmDialog), h(ItemMilestones, { item: project, canEdit })],
    })
    return mount(Host, {
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { template: '<a><slot /></a>' }, MarkdownView: true },
      },
    })
  }

  const settle = async (wrapper: ReturnType<typeof view>, check: () => boolean, what: string) =>
    until(async () => {
      await flushPromises()
      return check()
    }, what).then(() => wrapper)

  it('lists milestones in order on a timeline, marks the overdue one and counts the DoD', async () => {
    const wrapper = view()
    await settle(wrapper, () => wrapper.findAll('section.milestone').length === 2, 'the milestones')
    expect(wrapper.findAll('section.milestone').map((s) => s.find('.title').text())).toEqual([
      'Prototype built',
      'PoC decision',
    ])
    expect(wrapper.find('[data-testid="milestones-summary"]').text()).toBe('0 of 2 done')
    const timeline = wrapper.find('[data-testid="milestone-timeline"]')
    expect(timeline.findAll('.mark')).toHaveLength(2)
    expect(timeline.findAll('.mark.gate rect')).toHaveLength(1)
    expect(timeline.find('.mark.overdue').attributes('aria-label')).toContain('Prototype built')
    expect(wrapper.findAll('.overdue').some((o) => o.text().includes('Overdue'))).toBe(true)
    await settle(wrapper, () => wrapper.find('[data-testid="dod-progress"]').exists(), 'the DoD')
    expect(wrapper.find('[data-testid="dod-progress"]').text()).toBe('0 / 2')
    wrapper.unmount()
  })

  it('asks for a note before n/a, ticks a criterion and confirms done as an override', async () => {
    const wrapper = view()
    await settle(wrapper, () => wrapper.findAll('[data-testid^="dod-"] li').length === 2, 'the criteria')
    const [result, makeOrBuy] = await dodItemsOf(db, gate.id)

    const na = wrapper
      .findAll('button')
      .find((b) => b.attributes('aria-label') === 'Not applicable: Make or buy decided')!
    await na.trigger('click')
    await flushPromises()
    const form = wrapper.find('form.note-edit')
    await form.trigger('submit')
    expect(form.text()).toContain('n/a needs a note')
    await form.find('input').setValue('Bought: no make option')
    await form.trigger('submit')
    await until(async () => (await db.synced('dod_items').get(makeOrBuy!.id))?.state === 'na', 'n/a saved')
    expect(await db.synced('dod_items').get(makeOrBuy!.id)).toMatchObject({ note: 'Bought: no make option' })

    const box = wrapper.findAllComponents(Checkbox).find((c) => c.props('inputId') === `dod-${result!.id}`)!
    box.vm.$emit('update:modelValue', true)
    await until(async () => (await db.synced('dod_items').get(result!.id))?.state === 'done', 'done saved')
    await settle(wrapper, () => wrapper.find('[data-testid="dod-progress"]').text() === '1 / 1', 'the progress')

    // The DoD is complete, but no decision is recorded: marking the gate done is an override.
    const card = wrapper.find(`[data-testid="milestone-${gate.id}"]`)
    const select = wrapper.findAllComponents(Select).find((s) => card.element.contains(s.element))!
    select.vm.$emit('update:modelValue', 'done')
    await settle(wrapper, () => !!document.body.textContent?.includes('No gate decision is recorded'), 'the confirm')
    expect(document.body.textContent).toContain('The change is recorded in the History.')
    const accept = [...document.body.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Mark done anyway'),
    )!
    accept.click()
    await until(async () => (await db.synced('milestones').get(gate.id))?.status === 'done', 'the gate done')
    wrapper.unmount()
  })

  it('marks a milestone with a complete DoD done without asking', async () => {
    const wrapper = view()
    await settle(wrapper, () => wrapper.findAll('section.milestone').length === 2, 'the milestones')
    const first = wrapper.findAll('section.milestone')[0]!
    const select = wrapper.findAllComponents(Select).find((s) => first.element.contains(s.element))!
    select.vm.$emit('update:modelValue', 'done')
    const id = first.attributes('data-testid')!.replace('milestone-', '')
    await until(async () => (await db.synced('milestones').get(id))?.status === 'done', 'done')
    await settle(wrapper, () => wrapper.find('[data-testid="milestones-summary"]').text() === '1 of 2 done', 'count')
    wrapper.unmount()
  })

  it('is read-only without edit access', async () => {
    const wrapper = view(false)
    await settle(wrapper, () => wrapper.findAll('section.milestone').length === 2, 'the milestones')
    expect(wrapper.findAll('button').some((b) => b.text().includes('New milestone'))).toBe(false)
    expect(wrapper.findAllComponents(Select)).toHaveLength(0)
    expect(wrapper.findAllComponents(Checkbox).every((c) => c.props('disabled'))).toBe(true)
    expect(wrapper.find('form.add').exists()).toBe(false)
    wrapper.unmount()
  })
})
