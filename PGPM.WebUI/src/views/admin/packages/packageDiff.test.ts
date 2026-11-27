import { describe, expect, it } from 'vitest'
import type { PackageDiff, PackageDiffEntry } from '@/api/config'
import { applySummary, conflictKeys, groupDiff, keepSelection, readPackage, toggleKey } from './packageDiff'

const entry = (
  key: string,
  entity: string,
  action: string,
  extra: Partial<PackageDiffEntry> = {},
): PackageDiffEntry => ({
  key,
  entity,
  name: key.split(':')[1] ?? key,
  action,
  adopted: false,
  changed: [],
  ...extra,
})

const diff: PackageDiff = {
  name: 'innovation-ai',
  version: '1.0.0',
  entries: [
    entry('view:review_board_agenda', 'view', 'create'),
    entry('field:feasibility_index', 'field', 'conflict', { changed: ['label'] }),
    entry('field:impact_score', 'field', 'unchanged'),
    entry('status:idea.assessed', 'status', 'update', { changed: ['color'] }),
    entry('group:review_board', 'group', 'create', { adopted: true }),
    entry('field:problem', 'field', 'create'),
    entry('thing:x', 'gadget', 'unchanged'),
  ],
  counts: { create: 3, update: 1, unchanged: 2, conflict: 1 },
  needsAdmin: true,
  isAdmin: false,
}

describe('the dry run’s diff (docs/03 §17)', () => {
  it('groups the entries by entity in the importer’s order, conflicts first within a group', () => {
    const groups = groupDiff(diff)
    expect(groups.map((g) => g.label)).toEqual(['Groups', 'Statuses', 'Fields', 'Saved views', 'gadget'])
    const fields = groups.find((g) => g.entity === 'field')!
    expect(fields.entries.map((e) => e.key)).toEqual(['field:feasibility_index', 'field:problem', 'field:impact_score'])
    expect(fields.counts).toEqual({ create: 1, update: 0, unchanged: 1, conflict: 1 })
    expect(groupDiff(diff, { hideUnchanged: true }).map((g) => g.entity)).toEqual(['group', 'status', 'field', 'view'])
  })

  it('offers the conflicts for overwriting and keeps a selection valid across dry runs', () => {
    expect(conflictKeys(diff)).toEqual(['field:feasibility_index'])
    expect(toggleKey([], 'field:feasibility_index', true)).toEqual(['field:feasibility_index'])
    expect(toggleKey(['a', 'b'], 'a', false)).toEqual(['b'])
    // Run again with the overwrite: the conflict shows as an update and stays chosen; unknown keys go.
    const again: PackageDiff = {
      ...diff,
      entries: diff.entries.map((e) => (e.action === 'conflict' ? { ...e, action: 'update' } : e)),
    }
    expect(keepSelection(['field:feasibility_index', 'field:gone'], again)).toEqual(['field:feasibility_index'])
  })

  it('says what applying will do', () => {
    expect(applySummary(diff, [])).toBe('3 new, 1 updated, 1 kept (changed here)')
    expect(applySummary(diff, ['field:feasibility_index'])).toBe('3 new, 2 updated')
    const same = { ...diff, entries: diff.entries.filter((e) => e.action === 'unchanged') }
    expect(applySummary(same, [])).toBe('Nothing to apply.')
  })

  it('reads an uploaded package file', () => {
    expect(readPackage('{"format":"pgpm-config-package","name":"mine","version":"2.0.0"}')).toMatchObject({
      ok: true,
      name: 'mine',
      version: '2.0.0',
      title: 'mine',
    })
    expect(readPackage('nope')).toEqual({ ok: false, error: 'The file is not JSON.' })
    expect(readPackage('[]')).toEqual({ ok: false, error: 'Not a package.' })
    expect(readPackage('{"name":"x"}')).toEqual({ ok: false, error: 'Not a PGPM configuration package (format).' })
  })
})
