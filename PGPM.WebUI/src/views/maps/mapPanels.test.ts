import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Component } from 'vue'
import { useDatabase, type PgpmDb } from '@/local/db'
import { createMilestone } from '@/local/milestones'
import { Repo, setRepoContext } from '@/local/repo'
import type { DodItemRow, ItemRow, Stored } from '@/local/rows'
import { createConfiguredItem } from '@/local/templates'
import { seedConfig, templateId } from '@/test/configFixture'
import { memoryDb, until } from '@/test/memoryDb'
import MapItemSections from './MapItemSections.vue'
import MapMilestonePanel from './MapMilestonePanel.vue'

const { push } = vi.hoisted(() => ({ push: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query: {} }), useRouter: () => ({ push, replace: vi.fn() }) }))
const ANA = '01970000-0000-7000-8000-00000000a001'

/** The map side panel's phase-4 slots (docs/03 §13.7): an item's sections, a milestone's DoD and gate decision. */
describe('the map side panel’s sections and milestone panel', () => {
  let db: PgpmDb
  let repo: Repo
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: ANA, changed: null })
    repo = new Repo({ db: () => db, userId: () => ANA })
    // jsdom has no ResizeObserver (the auto-resizing Textarea) nor matchMedia (PrimeVue's Select).
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    )
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
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function view<C extends Component>(component: C, props: Record<string, unknown>) {
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(component, {
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
      },
    } as never)
    mounted.push(wrapper)
    return wrapper
  }

  it('shows an idea’s sections as on its Overview', async () => {
    const idea = await repo.transaction((tx) =>
      createConfiguredItem(tx, { type: 'idea', title: 'Invoice bot', statusId: 's-idea-new', shares: [] }),
    )
    const w = view(MapItemSections, { itemId: idea.id, canEdit: true })
    await until(() => w.findAll('.section-panel').length === 3, 'the sections')
    expect(w.text()).toContain('Feasibility')
  })

  it('edits a gate’s Definition of Done and records its decision, the project’s access deciding', async () => {
    const project = (await repo.transaction((tx) =>
      createConfiguredItem(tx, {
        type: 'project',
        title: 'AI pilot',
        statusId: 's-project-preparing',
        templateId,
        shares: [],
      }),
    )) as Stored<ItemRow>
    const gate = await createMilestone(repo, project.id, { kind: 'gate', title: 'Go live' })
    await repo.transaction((tx) =>
      tx.create<DodItemRow>('dod_items', {
        milestoneId: gate.id,
        text: 'Sponsor named',
        state: 'open',
        sortOrder: 'a0',
      }),
    )
    const w = view(MapMilestonePanel, { milestoneId: gate.id })
    await until(() => w.find('[aria-label="Done: Sponsor named"]').exists(), 'the DoD')
    await w.get('[aria-label="Done: Sponsor named"]').trigger('click')
    await until(
      async () => (await db.synced('dod_items').where('milestoneId').equals(gate.id).first())?.state === 'done',
      'the criterion done',
    )
    await flushPromises()
    const go = w.findAll('button').find((b) => b.text().trim() === 'Go')!
    await go.trigger('click')
    await w
      .findAll('button')
      .find((b) => b.text().trim() === 'Record decision')!
      .trigger('click')
    await until(async () => (await db.synced('milestones').get(gate.id))?.decision === 'go', 'the decision')
  })
})
