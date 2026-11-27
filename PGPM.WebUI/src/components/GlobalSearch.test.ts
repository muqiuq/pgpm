import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { reactive } from 'vue'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import { mountWithUi } from '@/test/mount'
import GlobalSearch from './GlobalSearch.vue'

const push = vi.fn()
const route = reactive({ path: '/notes', fullPath: '/notes' })
vi.mock('vue-router', () => ({ useRouter: () => ({ push }), useRoute: () => route }))

describe('the global search (docs/01 §3)', () => {
  let db: ReturnType<typeof memoryDb>

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    push.mockReset()
    await db.synced('items').bulkPut([
      { id: 'i1', type: 'idea', number: 1, title: 'Website chatbot', description: null, deleted: false },
      { id: 'p1', type: 'project', number: 2, title: 'Website relaunch', description: null, deleted: false },
    ])
    await db
      .synced('persons')
      .put({ id: 'pa', displayName: 'Wendy Webster', email: 'wendy@example.org', isActive: true, deleted: false })
  })
  afterEach(async () => {
    useDatabase(null)
    await db.delete()
  })

  it('lists items and persons as one listbox, moves with the arrows and opens with Enter', async () => {
    const wrapper = mountWithUi(GlobalSearch, { attachTo: document.body })
    const input = wrapper.get('input')
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-expanded')).toBe('false')
    await input.setValue('web')
    await until(() => wrapper.findAll('[role="option"]').length === 3, 'three results')
    expect(input.attributes('aria-expanded')).toBe('true')
    expect(wrapper.findAll('[role="group"]').map((g) => g.attributes('aria-label'))).toEqual(['Items', 'Persons'])
    expect(input.attributes('aria-activedescendant')).toBe('global-search-option-0')

    await input.trigger('keydown', { key: 'ArrowDown' })
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(input.attributes('aria-activedescendant')).toBe('global-search-option-2')
    expect(wrapper.get('#global-search-option-2').attributes('aria-selected')).toBe('true')
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(input.attributes('aria-activedescendant')).toBe('global-search-option-0')
    await input.trigger('keydown', { key: 'ArrowUp' })
    await input.trigger('keydown', { key: 'Enter' })
    expect(push).toHaveBeenCalledWith({ name: 'person', params: { id: 'pa' } })
    expect((input.element as HTMLInputElement).value).toBe('')
    wrapper.unmount()
  })

  it('opens a result by click, says when nothing is found, closes with Esc, focuses with Ctrl K', async () => {
    const wrapper = mountWithUi(GlobalSearch, { attachTo: document.body })
    const input = wrapper.get('input')
    await input.setValue('nothing like it')
    await until(() => wrapper.text().includes('Nothing found'), 'the empty state')
    await input.setValue('relaunch')
    await until(() => wrapper.findAll('[role="option"]').length === 1, 'one result')
    await wrapper.get('[role="option"]').trigger('mousedown')
    expect(push).toHaveBeenCalledWith({ name: 'project', params: { id: 'p1' } })

    await input.setValue('chatbot')
    await until(() => wrapper.findAll('[role="option"]').length === 1, 'the idea')
    await input.trigger('keydown', { key: 'Escape' })
    expect(input.attributes('aria-expanded')).toBe('false')

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }))
    expect(document.activeElement).toBe(input.element)
    wrapper.unmount()
  })

  it('stays open while the page writes its query (a list’s search), closes on another page', async () => {
    const wrapper = mountWithUi(GlobalSearch, { attachTo: document.body })
    const input = wrapper.get('input')
    await input.setValue('web')
    await until(() => wrapper.findAll('[role="option"]').length === 3, 'three results')
    route.fullPath = '/persons?search=partner'
    await wrapper.vm.$nextTick()
    expect(input.attributes('aria-expanded')).toBe('true')
    route.path = '/ideas'
    route.fullPath = '/ideas'
    await wrapper.vm.$nextTick()
    expect(input.attributes('aria-expanded')).toBe('false')
    route.path = '/notes'
    route.fullPath = '/notes'
    wrapper.unmount()
  })
})
