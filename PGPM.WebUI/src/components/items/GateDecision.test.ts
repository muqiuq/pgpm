import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDatabase } from '@/local/db'
import { createItem } from '@/local/items'
import { createMilestone } from '@/local/milestones'
import { Repo } from '@/local/repo'
import type { ItemRow, MilestoneRow, StatusRow } from '@/local/rows'
import { memoryDb, until } from '@/test/memoryDb'
import { mountWithUi } from '@/test/mount'
import GateDecision from './GateDecision.vue'

// jsdom has no ResizeObserver, which the auto-resizing Textarea uses.
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

const ANA = '01970000-0000-7000-8000-00000000a001'
const T1 = '01970000-0000-7000-8000-0000000000e1'

function status(id: string, name: string, extra: Partial<StatusRow> = {}): StatusRow {
  return {
    id,
    seq: 1,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: '2026-11-01T00:00:00Z',
    createdBy: null,
    updatedAt: '2026-11-01T00:00:00Z',
    updatedBy: null,
    itemType: 'project',
    name,
    color: '#64748b',
    icon: null,
    category: 'active',
    sortOrder: 10,
    isDefault: false,
    maxDays: null,
    isArchived: false,
    ...extra,
  }
}

describe('GateDecision (docs/01 §11)', () => {
  let db: ReturnType<typeof memoryDb>
  let repo: Repo
  let project: ItemRow
  let gate: MilestoneRow

  beforeEach(async () => {
    db = memoryDb()
    useDatabase(db)
    repo = new Repo({ db: () => db, userId: () => ANA })
    for (const s of [
      status('s-poc', 'Prototype / PoC'),
      status('s-mvp', 'MVP / pilot'),
      status('s-stopped', 'Stopped', { category: 'closed' }),
      status('s-other', 'Other'),
    ])
      await db.synced('statuses').put(s)
    // The template allows PoC and MVP only: Stopped is not suggested.
    for (const [id, statusId] of [
      ['ts1', 's-poc'],
      ['ts2', 's-mvp'],
    ])
      await db.synced('template_statuses').put({ id, templateId: T1, statusId, deleted: false, seq: 1, rev: 1 })
    project = await repo.transaction((tx) =>
      createItem(tx, {
        type: 'project',
        title: 'Chat bot',
        statusId: 's-poc',
        shares: [],
        values: { projectTemplateId: T1 },
      }),
    )
    gate = await createMilestone(repo, project.id, {
      kind: 'gate',
      title: 'PoC decision',
      onGoStatusId: 's-mvp',
      onNoGoStatusId: 's-stopped',
    })
  })
  afterEach(async () => {
    useDatabase(null)
    await db.delete()
  })

  const mountGate = (canEdit = true) =>
    mountWithUi(GateDecision, { props: { milestone: gate, project, canEdit, repo }, attachTo: document.body })
  const button = (wrapper: ReturnType<typeof mountGate>, text: string) =>
    wrapper.findAll('button').find((b) => b.text().trim() === text)!

  it('records go with a note and moves the project to the suggested status on confirmation', async () => {
    const wrapper = mountGate()
    await until(async () => {
      await flushPromises()
      return wrapper.find('[role="radiogroup"]').exists()
    }, 'the decision')
    expect(button(wrapper, 'Record decision').attributes('disabled')).toBeDefined()
    await button(wrapper, 'Go').trigger('click')
    expect(button(wrapper, 'Go').attributes('aria-checked')).toBe('true')
    await wrapper.find('textarea').setValue('Accuracy 92 %')
    await button(wrapper, 'Record decision').trigger('click')
    await until(async () => (await db.synced('milestones').get(gate.id))?.decision === 'go', 'the decision saved')
    expect(await db.synced('milestones').get(gate.id)).toMatchObject({ decisionNote: 'Accuracy 92 %' })

    await until(async () => {
      await flushPromises()
      return wrapper.find('[data-testid="status-offer"]').exists()
    }, 'the offer')
    expect(wrapper.find('[data-testid="status-offer"]').text()).toContain('MVP / pilot')
    // Nothing changes before the user confirms.
    expect((await db.synced('items').get(project.id))?.statusId).toBe('s-poc')
    await button(wrapper, 'Move project to MVP / pilot').trigger('click')
    await until(async () => (await db.synced('items').get(project.id))?.statusId === 's-mvp', 'the project moved')
    await flushPromises()
    expect(wrapper.find('[data-testid="status-offer"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('offers nothing for a status the template doesn’t allow, nor for iterate', async () => {
    const wrapper = mountGate()
    await flushPromises()
    await button(wrapper, 'No-go').trigger('click')
    await button(wrapper, 'Record decision').trigger('click')
    await until(async () => (await db.synced('milestones').get(gate.id))?.decision === 'no_go', 'no-go saved')
    await flushPromises()
    expect(wrapper.find('[data-testid="status-offer"]').exists()).toBe(false)

    await button(wrapper, 'Iterate').trigger('click')
    await button(wrapper, 'Record decision').trigger('click')
    await until(async () => (await db.synced('milestones').get(gate.id))?.decision === 'iterate', 'iterate saved')
    await flushPromises()
    expect(wrapper.find('[data-testid="status-offer"]').exists()).toBe(false)
    expect((await db.synced('items').get(project.id))?.statusId).toBe('s-poc')
    wrapper.unmount()
  })

  it('is read-only without edit access', async () => {
    const wrapper = mountGate(false)
    await flushPromises()
    expect(button(wrapper, 'Go').attributes('disabled')).toBeDefined()
    expect(wrapper.find('textarea').exists()).toBe(false)
    expect(wrapper.findAll('button').some((b) => b.text().includes('Record decision'))).toBe(false)
    wrapper.unmount()
  })
})
