import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeResponse } from '@/api/auth'
import { useDatabase } from '@/local/db'
import { setRepoContext } from '@/local/repo'
import { useSession } from '@/stores/session'
import { memoryDb, until } from '@/test/memoryDb'
import GateDecisionsView from './GateDecisionsView.vue'

const { route, replace, push } = vi.hoisted(() => ({
  route: { query: {} as Record<string, string>, fullPath: '/gates' },
  replace: vi.fn(),
  push: vi.fn(),
}))
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push, replace }) }))

const synced = { seq: 1, rev: 1, deleted: false, deleteBatchId: null, _local: { serverRev: 1, pending: false } }
const at = '2026-11-20T10:00:00Z'
const project = (id: string, number: number, title: string) => ({
  ...synced,
  id,
  type: 'project',
  number,
  title,
  description: null,
  ownerUserId: 'u-ana',
  statusId: 's',
  projectTemplateId: null,
  access: 'edit',
  createdAt: at,
  updatedAt: at,
})
const gate = (id: string, itemId: string, title: string, extra: Record<string, unknown> = {}) => ({
  ...synced,
  id,
  itemId,
  kind: 'gate',
  title,
  description: null,
  dueDate: null,
  status: 'planned',
  decision: 'pending',
  decisionNote: null,
  decidedAt: null,
  decidedBy: null,
  sortOrder: 'a0',
  createdAt: at,
  updatedAt: at,
  ...extra,
})

describe('the Gate decisions list (docs/01 §11)', () => {
  let db: ReturnType<typeof memoryDb>
  const mounted: { unmount(): void }[] = []

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    setRepoContext({ userId: 'u-ana', changed: null })
    route.query = {}
    push.mockReset()
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    await db.synced('items').bulkPut([project('p1', 1, 'Invoice bot'), project('p2', 2, 'Chat bot')])
    await db
      .synced('milestones')
      .bulkPut([
        gate('g1', 'p1', 'PoC decision', { decision: 'go', decidedAt: '2026-11-21T09:00:00.000000Z' }),
        gate('g2', 'p2', 'Scale decision', { decision: 'no_go', decidedAt: '2026-11-25T09:00:00.000000Z' }),
        gate('g3', 'p2', 'Impact review'),
        { ...gate('m1', 'p1', 'Prototype built'), kind: 'milestone', decision: null },
      ])
    await db.synced('saved_views').put({
      ...synced,
      id: 'v-gates',
      ownerUserId: 'u-boss',
      itemType: 'gate',
      name: 'Gate decisions',
      isShared: true,
      sortOrder: 140,
      definition: {
        search: '',
        filters: { decisions: ['go', 'no_go', 'iterate', 'pivot', 'park'] },
        sort: ['-decided_at'],
        columns: ['project', 'title', 'decision', 'decided_at', 'decided_by', 'due_date', 'dod_progress'],
      },
    })
  })
  afterEach(async () => {
    for (const w of mounted.splice(0)) w.unmount()
    vi.unstubAllGlobals()
    setRepoContext({ userId: null })
    useDatabase(null)
    await db.delete()
  })

  function open() {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSession().set({
      user: { id: 'u-ana', displayName: 'Ana', groupIds: [], isAdmin: false, rights: {}, personId: null },
      flags: { mustChangePassword: false, totpSetupRequired: false },
      session: { totpDue: false },
    } as unknown as MeResponse)
    const wrapper = mount(GateDecisionsView, {
      attachTo: document.body,
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService, pinia],
        directives: { tooltip: Tooltip },
        stubs: { RouterLink: { props: ['to'], template: '<a><slot /></a>' } },
      },
    })
    mounted.push(wrapper)
    return wrapper
  }

  const rows = () => [...document.body.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '')

  it('lists every gate of the projects on the device, not plain milestones', async () => {
    open()
    await until(() => rows().length === 3, 'the gates')
    expect(rows().join('|')).not.toContain('Prototype built')
    expect(rows().find((r) => r.includes('PoC decision'))).toContain('P-0001')
    expect(rows().find((r) => r.includes('Impact review'))).toContain('Pending')
  })

  it('applies the seeded view: decided gates, newest first', async () => {
    route.query = { view: 'v-gates' }
    open()
    await until(() => rows().length === 2 && rows()[0]!.includes('Scale decision'), 'the decided gates')
    expect(rows()[0]).toContain('No-go')
    expect(rows()[1]).toContain('PoC decision')
    expect(document.body.querySelector('[data-testid="saved-views"]')!.textContent).toContain('Gate decisions')
  })
})
