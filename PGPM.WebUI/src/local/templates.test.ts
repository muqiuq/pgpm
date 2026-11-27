import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  feasibilityKeys,
  fields as fieldRows,
  G_BOARD,
  G_TEAM,
  playbookRatings,
  seedConfig,
  statuses,
  templateId,
} from '@/test/configFixture'
import { memoryDb } from '@/test/memoryDb'
import { loadCatalogue, loadTemplates } from './catalogue'
import type { PgpmDb } from './db'
import { addSection, fieldValuesOf, itemSectionsOf, removeSection, scoreOf, setFieldValue, valuesByKey } from './fields'
import { bulkPatch, convertToProject, createItem } from './items'
import { Repo } from './repo'
import type { DodItemRow, FieldValueRow, ItemRow, MilestoneRow, Stored } from './rows'
import { allowedStatuses, createConfiguredItem, templateShares, templateStatus } from './templates'

const ANA = '01970000-0000-7000-8000-00000000a001'

describe('sections, values and templates on the device (docs/01 §6.2, §10.2)', () => {
  let db: PgpmDb
  let repo: Repo

  beforeEach(async () => {
    db = memoryDb()
    repo = new Repo({ db: () => db, userId: () => ANA })
    await seedConfig(db)
  })
  afterEach(async () => {
    await db.delete()
  })

  const idea = () =>
    repo.transaction((tx) =>
      createConfiguredItem(tx, { type: 'idea', title: 'Invoice bot', statusId: 's-idea-new', shares: [] }),
    )
  const sectionNames = async (itemId: string) => {
    const catalogue = await loadCatalogue(db)
    return (await itemSectionsOf(db, itemId)).map((s) => catalogue.sectionById.get(s.sectionId)!.name)
  }

  it('gives a new idea the sections default for ideas, in one batch with the item', async () => {
    const item = await idea()
    expect(await sectionNames(item.id)).toEqual(['Value', 'Feasibility', 'Review board decision'])
    const outbox = await db.outbox.toArray()
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    expect(outbox.map((e) => `${e.op} ${e.table}`)).toEqual([
      'insert items',
      'insert item_sections',
      'insert item_sections',
      'insert item_sections',
    ])
  })

  it('adds and removes sections (values stay), and refuses what doesn’t fit or isn’t editable', async () => {
    const item = await idea()
    const catalogue = await loadCatalogue(db)
    const charter = catalogue.sectionById.get('sec-charter')!
    const old = catalogue.sectionById.get('sec-old')!
    const rb = catalogue.sectionById.get('sec-rb')!
    await expect(repo.transaction((tx) => addSection(tx, item.id, charter, catalogue))).rejects.toThrow(
      'doesn’t apply to ideas',
    )
    await expect(repo.transaction((tx) => addSection(tx, item.id, old, catalogue))).rejects.toThrow()
    expect(
      await repo.transaction((tx) => addSection(tx, item.id, catalogue.sectionById.get('sec-feas')!, catalogue)),
    ).toBe(false)

    const infra = catalogue.fieldByKey.get('feas_infra')!
    await repo.transaction((tx) => setFieldValue(tx, item.id, infra, 4))
    const feasRow = (await itemSectionsOf(db, item.id)).find((s) => s.sectionId === 'sec-feas')!
    await repo.transaction((tx) => removeSection(tx, feasRow.id))
    expect(await sectionNames(item.id)).toEqual(['Value', 'Review board decision'])
    expect((await fieldValuesOf(db, item.id)).map((v) => v.value)).toEqual([4])
    await repo.transaction((tx) => addSection(tx, item.id, catalogue.sectionById.get('sec-feas')!, catalogue))
    expect(await sectionNames(item.id)).toEqual(['Value', 'Review board decision', 'Feasibility'])

    // The review board's section: read-only for this user (canEdit false), as the server would refuse it.
    const decision = catalogue.fieldByKey.get('rb_decision')!
    await expect(repo.transaction((tx) => setFieldValue(tx, item.id, decision, 'go', { section: rb }))).rejects.toThrow(
      'Only some groups may edit Review board decision',
    )
    // Read access: nothing changes.
    await db.synced('items').put({ ...(await db.synced('items').get(item.id))!, access: 'read' })
    await expect(repo.transaction((tx) => setFieldValue(tx, item.id, infra, 5))).rejects.toThrow('not change it')
  })

  it('inserts a value once, then patches it; refuses a value that doesn’t fit; computes the score live', async () => {
    const item = await idea()
    const catalogue = await loadCatalogue(db)
    await db.outbox.clear()
    const infra = catalogue.fieldByKey.get('feas_infra')!
    expect(await repo.transaction((tx) => setFieldValue(tx, item.id, infra, 3))).toBe('inserted')
    expect(await repo.transaction((tx) => setFieldValue(tx, item.id, infra, 4))).toBe('patched')
    expect(await repo.transaction((tx) => setFieldValue(tx, item.id, infra, 4))).toBe('unchanged')
    await expect(repo.transaction((tx) => setFieldValue(tx, item.id, infra, 7))).rejects.toThrow(
      'An integer from 0 to 5',
    )
    const outbox = await db.outbox.toArray()
    // The patch coalesced into the unsent insert.
    expect(outbox.map((e) => `${e.op} ${e.table}`)).toEqual(['insert field_values'])
    expect(outbox[0]!.row).toMatchObject({ itemId: item.id, fieldId: infra.id, value: 4 })

    const index = catalogue.fieldByKey.get('feasibility_index')!
    const score = async () => scoreOf(catalogue, index, valuesByKey(catalogue, await fieldValuesOf(db, item.id)))
    expect(await score()).toBeNull()
    for (const key of feasibilityKeys) {
      await repo.transaction((tx) => setFieldValue(tx, item.id, catalogue.fieldByKey.get(key)!, playbookRatings[key]))
    }
    expect(await score()).toBeNull()
    const time = catalogue.fieldByKey.get('time_to_proof_of_value')!
    await repo.transaction((tx) => setFieldValue(tx, item.id, time, 'lt3'))
    expect(await score()).toBe(58)
    // Two ratings of 5 n/a: answered max 55, raw 48 → 48 × 65 / 55 = 56.7 → 57 (docs/03 §16).
    await repo.transaction((tx) => setFieldValue(tx, item.id, catalogue.fieldByKey.get('feas_data_access')!, 'na'))
    await repo.transaction((tx) => setFieldValue(tx, item.id, catalogue.fieldByKey.get('feas_solution_known')!, 'na'))
    expect(await score()).toBe(57)
    // Clearing a value makes it incomplete again.
    await repo.transaction((tx) => setFieldValue(tx, item.id, time, null))
    expect(await score()).toBeNull()
  })

  it('applies a template offline in one batch: status, shares, sections, defaults, milestones with due dates', async () => {
    const templates = await loadTemplates(db)
    const parts = templates.byId.get(templateId)!
    expect(allowedStatuses(statuses, parts).map((s) => s.name)).toEqual([
      'Preparing test',
      'Prototype / PoC',
      'MVP / pilot',
      'Stopped',
    ])
    expect(allowedStatuses(statuses, null).map((s) => s.name)).toContain('Draft')
    expect(templateStatus(statuses, parts)?.name).toBe('Preparing test')
    expect(templateStatus(statuses, null)?.name).toBe('Draft')
    expect(templateShares(parts, new Set([G_TEAM]))).toEqual([{ groupId: G_TEAM, access: 'edit' }])

    const project = await repo.transaction((tx) =>
      createConfiguredItem(tx, {
        type: 'project',
        title: 'Invoice recognition',
        statusId: 's-project-preparing',
        shares: templateShares(parts, new Set([G_TEAM, G_BOARD])),
        templateId,
        startDate: '2026-12-01',
      }),
    )
    expect(project).toMatchObject({ projectTemplateId: templateId, projectStartDate: '2026-12-01' })
    expect(await sectionNames(project.id)).toEqual(['Value', 'Feasibility', 'Charter', 'Data & platform'])
    const catalogue = await loadCatalogue(db)
    const values = valuesByKey(catalogue, await fieldValuesOf(db, project.id))
    // The template's default and the field's own default value.
    expect(values).toEqual({ is_ai_system: true, scope: 'To be defined' })

    const milestones = (
      (await db.synced('milestones').where('itemId').equals(project.id).toArray()) as unknown as MilestoneRow[]
    ).sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : 1))
    expect(milestones.map((m) => [m.kind, m.title, m.dueDate, m.decision, m.onGoStatusId])).toEqual([
      ['gate', 'Ready for test', '2026-12-01', 'pending', 's-project-poc'],
      ['milestone', 'Prototype built', '2026-12-29', null, null],
      ['gate', 'PoC decision', '2027-01-26', 'pending', 's-project-mvp'],
    ])
    const dod = ((await db.synced('dod_items').toArray()) as unknown as DodItemRow[]).sort((a, b) =>
      a.sortOrder < b.sortOrder ? -1 : 1,
    )
    expect(dod.filter((d) => d.milestoneId === milestones[0]!.id).map((d) => [d.text, d.state])).toEqual([
      ['Key persons defined', 'open'],
      ['Test phase prepared', 'open'],
    ])

    const outbox = await db.outbox.toArray()
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    const counts = outbox.reduce<Record<string, number>>((acc, e) => {
      acc[`${e.op} ${e.table}`] = (acc[`${e.op} ${e.table}`] ?? 0) + 1
      return acc
    }, {})
    expect(counts).toEqual({
      'insert items': 1,
      'insert item_shares': 2,
      'insert item_sections': 4,
      'insert field_values': 2,
      'insert milestones': 3,
      'insert dod_items': 5,
    })
    // Server-owned members never leave the device.
    const gate = outbox.find((e) => e.table === 'milestones')!.row!
    expect(gate).not.toHaveProperty('decidedAt')
    expect(gate).not.toHaveProperty('completedAt')
  })

  it('a bulk status change skips the projects whose template doesn’t allow the status', async () => {
    const [ai, blank] = await repo.transaction(async (tx) => [
      await createConfiguredItem(tx, {
        type: 'project',
        title: 'AI',
        statusId: 's-project-preparing',
        templateId,
        shares: [],
      }),
      await createConfiguredItem(tx, { type: 'project', title: 'Blank', statusId: 's-project-draft', shares: [] }),
    ])
    expect(await bulkPatch(repo, [ai.id, blank.id], { statusId: 's-project-active' })).toEqual({
      changed: 1,
      skipped: 1,
    })
    expect((await db.synced('items').get(blank.id))?.statusId).toBe('s-project-active')
    expect(await bulkPatch(repo, [ai.id, blank.id], { statusId: 's-project-poc' })).toEqual({ changed: 2, skipped: 0 })
  })

  it('makes a blank project with the sections default for projects (none here) and no milestones', async () => {
    const project = await repo.transaction((tx) =>
      createConfiguredItem(tx, { type: 'project', title: 'Blank', statusId: 's-project-draft', shares: [] }),
    )
    expect(project.projectTemplateId).toBeNull()
    expect(await itemSectionsOf(db, project.id)).toEqual([])
    expect(await db.synced('milestones').count()).toBe(0)
  })

  it('converts an idea with a template: the sections and values that apply to projects come along', async () => {
    const item = await idea()
    const catalogue = await loadCatalogue(db)
    for (const key of feasibilityKeys)
      await repo.transaction((tx) => setFieldValue(tx, item.id, catalogue.fieldByKey.get(key)!, playbookRatings[key]))
    await repo.transaction((tx) =>
      setFieldValue(tx, item.id, catalogue.fieldByKey.get('time_to_proof_of_value')!, 'lt3'),
    )
    await repo.transaction((tx) =>
      setFieldValue(tx, item.id, catalogue.fieldByKey.get('strategic_advantages')!, ['efficiency']),
    )
    // An idea-only value (the review board's), put there by someone allowed to: it stays with the idea.
    await db.synced('field_values').put({
      id: 'fv-rb',
      itemId: item.id,
      fieldId: 'f-rb_decision',
      value: 'go',
      seq: 1,
      rev: 1,
      deleted: false,
      deleteBatchId: null,
      createdAt: '2026-11-01T00:00:00Z',
      createdBy: null,
      updatedAt: '2026-11-01T00:00:00Z',
      updatedBy: null,
    } satisfies FieldValueRow)
    await db.outbox.clear()

    const result = await convertToProject(repo, item.id, statuses, { templateId, startDate: '2026-12-01' })
    const project = result.project
    expect(project).toMatchObject({
      statusId: 's-project-preparing',
      projectTemplateId: templateId,
      originItemId: item.id,
    })
    expect(await sectionNames(project.id)).toEqual(['Value', 'Feasibility', 'Charter', 'Data & platform'])
    const values = valuesByKey(catalogue, await fieldValuesOf(db, project.id))
    expect(values).toMatchObject({
      ...playbookRatings,
      time_to_proof_of_value: 'lt3',
      strategic_advantages: ['efficiency'],
      is_ai_system: true,
    })
    expect(values).not.toHaveProperty('rb_decision')
    expect(scoreOf(catalogue, catalogue.fieldByKey.get('feasibility_index')!, values)).toBe(58)
    expect(result.copied).toMatchObject({ sections: 4, milestones: 3 })
    expect(result.copied.values).toBe(16)
    expect((await db.synced('items').get(item.id)) as Stored<ItemRow>).toMatchObject({ statusId: 's-idea-selected' })
    const outbox = await db.outbox.toArray()
    expect(new Set(outbox.map((e) => e.batch)).size).toBe(1)
    expect(outbox.some((e) => e.table === 'field_values' && (e.row as FieldValueRow).fieldId === 'f-rb_decision')).toBe(
      false,
    )

    const other = await repo.transaction((tx) =>
      createItem(tx, { type: 'idea', title: 'Other', statusId: 's-idea-new', shares: [] }),
    )
    await expect(convertToProject(repo, other.id, statuses, { templateId: 'missing' })).rejects.toThrow(
      'template is not on this device',
    )
    expect(fieldRows.some((f) => f.key === 'rb_decision')).toBe(true)
  })
})
