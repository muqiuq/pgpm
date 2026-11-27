import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { loadCatalogue } from '@/local/catalogue'
import { useDatabase, type PgpmDb } from '@/local/db'
import { fieldValuesOf, itemSectionsOf, setFieldValue } from '@/local/fields'
import { Repo, setRepoContext } from '@/local/repo'
import type { ItemRow, Stored } from '@/local/rows'
import { createConfiguredItem } from '@/local/templates'
import { FEASIBILITY_AREAS, feasibilityKeys, playbookRatings, seedConfig } from '@/test/configFixture'
import { memoryDb, until } from '@/test/memoryDb'
import ItemSections from './ItemSections.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }), useRouter: () => ({ push, replace: vi.fn() }) }))

const ANA = '01970000-0000-7000-8000-00000000a001'

describe('the sections of an item (docs/01 §6.2, docs/03 §12.7)', () => {
  let db: PgpmDb
  let repo: Repo
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: ANA, changed: null })
    repo = new Repo({ db: () => db, userId: () => ANA })
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    await seedConfig(db)
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    // PrimeVue's tooltips remove themselves on a timer after the unmount: before the test's document goes.
    await new Promise((resolve) => setTimeout(resolve, 5))
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  async function idea(): Promise<Stored<ItemRow>> {
    const item = await repo.transaction((tx) =>
      createConfiguredItem(tx, { type: 'idea', title: 'Invoice bot', statusId: 's-idea-new', shares: [] }),
    )
    return (await db.synced('items').get(item.id)) as Stored<ItemRow>
  }

  function view(item: ItemRow, canEdit = true) {
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(ItemSections, {
      props: { item, canEdit },
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
      },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const titles = (w: ReturnType<typeof view>) => w.findAll('.section-panel .toggle span').map((s) => s.text())

  it('shows the sections in order; one editable only by the review board is read-only, with a lock', async () => {
    const item = await idea()
    const w = view(item)
    await until(() => titles(w).length === 3, 'the sections')
    expect(titles(w)).toEqual(['Value', 'Feasibility', 'Review board decision'])
    const board = w.get('[data-testid="section-sec-rb"]')
    expect(board.find('[aria-label="Read-only for you"]').exists()).toBe(true)
    // A read-only select is shown as its value, not as a picker.
    expect(board.find('[role="combobox"]').exists()).toBe(false)
    expect(w.get('[data-testid="section-sec-feas"]').text()).toContain('Twelve statements rated 0–5.')
    // Every statement names its area; each area's heading is shown once, above its first statement.
    const headings = w.findAll('[data-testid="section-sec-feas"] dt.subheading').map((h) => h.text())
    expect(headings).toEqual(FEASIBILITY_AREAS)
  })

  it('rates with one click and shows the feasibility index live: 58 / 65 for the playbook example', async () => {
    const item = await idea()
    const catalogue = await loadCatalogue(db)
    for (const key of feasibilityKeys.slice(1))
      await repo.transaction((tx) => setFieldValue(tx, item.id, catalogue.fieldByKey.get(key)!, playbookRatings[key]))
    await repo.transaction((tx) =>
      setFieldValue(tx, item.id, catalogue.fieldByKey.get('time_to_proof_of_value')!, 'lt3'),
    )
    const w = view(item)
    const feas = () => w.find('[data-testid="section-sec-feas"]')
    await until(() => feas().exists() && feas().text().includes('incomplete'), 'the incomplete index')

    await feas().get('[aria-label^="Statement 1: fully applies,"]').trigger('click')
    await until(() => feas().text().includes('58 / 65'), 'the index')
    expect(feas().get('[aria-label^="Statement 1: fully applies,"]').attributes('aria-pressed')).toBe('true')
    expect((await fieldValuesOf(db, item.id)).find((v) => v.fieldId === 'f-feas_data_access')?.value).toBe(5)

    // n/a: rescaled to 65 – raw 53 of 60 → 57.4 → 57.
    await feas().get('[aria-label^="Statement 1: not applicable,"]').trigger('click')
    await until(() => feas().text().includes('57 / 65'), 'the rescaled index')
    // A click on the chosen value clears it: incomplete again.
    await feas().get('[aria-label^="Statement 1: not applicable,"]').trigger('click')
    await until(() => feas().text().includes('incomplete'), 'incomplete again')
  })

  it('adds a section that applies to the type; read access shows no actions', async () => {
    const item = await idea()
    const w = view(item)
    await until(() => titles(w).length === 3, 'the sections')
    // Nothing else applies to ideas here (Charter and Data & platform are for projects, Old section is archived).
    expect(w.findAll('button').some((b) => b.text() === 'Add section')).toBe(false)
    const row = (await itemSectionsOf(db, item.id)).find((s) => s.sectionId === 'sec-value')!
    await repo.remove('item_sections', row.id)
    await until(() => titles(w).length === 2, 'the section removed')
    const add = w.findAll('button').find((b) => b.text() === 'Add section')!
    await add.trigger('click')
    await until(() => !!document.body.querySelector('#add-section-menu'), 'the menu')
    const entry = [...document.body.querySelectorAll('#add-section-menu [role="menuitem"]')].find((e) =>
      e.textContent?.includes('Value'),
    ) as HTMLElement
    entry.querySelector('a, div')?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    ;(entry.firstElementChild as HTMLElement | null)?.click()
    await until(() => titles(w).includes('Value'), 'the section added')
    expect(titles(w)).toEqual(['Feasibility', 'Review board decision', 'Value'])

    for (const x of mounted.splice(0)) x.unmount()
    const read = view({ ...item, access: 'read' }, false)
    await until(() => titles(read).length === 3, 'the read-only sections')
    expect(read.findAll('button').some((b) => b.text() === 'Add section')).toBe(false)
    expect(read.find('[aria-label^="More actions for"]').exists()).toBe(false)
    expect(read.get('[aria-label^="Statement 1: fully applies,"]').attributes('disabled')).toBeDefined()
  })
})
