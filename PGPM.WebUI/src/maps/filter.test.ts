import { describe, expect, it } from 'vitest'
import { activeConditions, elementShown, visibleIds, type FilterContext } from './filter'
import { ASSESSED, C1, C2, E1, IDEA, ITEMS, L1, NOTE, P1, PROJECT, R1, element, elements, uid } from './testFixtures'
import type { ElementRow } from './types'

const ctx: FilterContext = {
  itemsById: new Map(ITEMS.map((i) => [i.id, i])),
  personsByItem: new Map([[IDEA, new Set(['anna'])]]),
  quadrantOf: (id) => (id === IDEA ? 'tr' : null),
  valueOf: (id, key) => (id === IDEA && key === 'feasibility' ? 7 : null),
}

const all = elements()
const shown = (filter: Parameters<typeof visibleIds>[1], rows: ElementRow[] = all) => [...visibleIds(rows, filter, ctx)]

describe('visibility filter', () => {
  it('shows everything without conditions', () => {
    expect(shown(null)).toHaveLength(all.length)
    expect(shown({})).toHaveLength(all.length)
    expect(activeConditions({})).toBe(0)
  })

  it('filters by type: item types, milestones, drawings', () => {
    expect(shown({ types: ['project'] })).toEqual([P1])
    expect(shown({ types: ['drawing'] }).sort()).toEqual([R1, E1, uid('e6')].sort())
    expect(shown({ types: ['idea', 'drawing'] })).toContain(C1)
    expect(activeConditions({ types: ['idea'] })).toBe(1)
  })

  it('hides a line when an element it is connected to is hidden', () => {
    // The line connects card C1 (idea) and rectangle R1.
    expect(shown({ types: ['drawing'] })).not.toContain(L1)
    expect(shown({ types: ['idea', 'drawing'] })).toContain(L1)
    expect(shown({ types: ['project', 'drawing'] })).not.toContain(L1)
    expect(shown({ statusIds: [ASSESSED] })).toContain(L1)
  })

  it('filters cards by status, person, colour, quadrant and score range', () => {
    expect(shown({ statusIds: [ASSESSED] })).toEqual(expect.arrayContaining([C1, C2, R1]))
    expect(shown({ statusIds: [ASSESSED] })).not.toContain(P1)
    expect(shown({ personIds: ['anna'] })).not.toContain(P1)
    expect(shown({ personIds: ['ben'] })).not.toContain(C1)
    expect(shown({ colors: ['purple'] })).toContain(C1)
    expect(shown({ colors: ['default'] })).toEqual(expect.arrayContaining([P1]))
    expect(shown({ colors: ['default'] })).not.toContain(C1)
    expect(shown({ quadrants: ['tr'] })).toContain(C1)
    expect(shown({ quadrants: ['bl'] })).not.toContain(C1)
    expect(shown({ score: { field: 'feasibility', min: 5 } })).toContain(C1)
    expect(shown({ score: { field: 'feasibility', min: 8 } })).not.toContain(C1)
    expect(shown({ score: { field: 'feasibility', max: 7 } })).toContain(C1)
    // Drawings don't have items: item conditions don't hide them.
    expect(shown({ statusIds: ['none'] })).toContain(E1)
  })

  it('shows a restricted card only while nothing asks about its item', () => {
    const restricted = element(uid('r9'), { kind: 'item', item_id: uid('gone') })
    expect(elementShown(restricted, {}, ctx)).toBe(true)
    expect(elementShown(restricted, { types: ['note'] }, ctx)).toBe(true)
    expect(elementShown(restricted, { types: ['drawing'] }, ctx)).toBe(false)
    expect(elementShown(restricted, { statusIds: [ASSESSED] }, ctx)).toBe(false)
  })

  it('a note has no status: a status condition hides it', () => {
    const note = element(uid('n9'), { kind: 'item', item_id: NOTE })
    expect(elementShown(note, { statusIds: [ASSESSED] }, ctx)).toBe(false)
    expect(elementShown(note, { types: ['note'] }, ctx)).toBe(true)
    expect(ctx.itemsById.get(PROJECT)?.type).toBe('project')
  })
})
