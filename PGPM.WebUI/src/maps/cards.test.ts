import { describe, expect, it } from 'vitest'
import { cssColor, dodProgress, itemColor, itemKey, milestoneOverdue, milestoneTone } from './cards'
import { commandFor } from './keyboard'
import { C1, IDEA, PROJECT, setup, uid } from './testFixtures'

describe('card models', () => {
  it('shows key, status, colour, persons and the configured badges', () => {
    const s = setup({
      map: {
        id: 'm',
        kind: 'canvas',
        name: 'M',
        settings: { cardBadges: ['feasibility', 'impact', 'feas_a', 'missing'] },
      },
      assignments: [{ id: uid('a1'), item_id: IDEA, person_id: 'anna' }],
    })
    const card = s.model.cards.value.get(IDEA)!
    expect(card).toMatchObject({
      key: 'I-7',
      title: 'Chatbot',
      color: 'var(--pgpm-note-purple)',
      status: { name: 'Assessed', category: 'open' },
      overdue: false,
      persons: 1,
      progress: null,
    })
    expect(card.badges).toEqual([
      { key: 'feasibility', label: 'Feasibility', text: '7 / 10' },
      { key: 'impact', label: 'Impact', text: '80' },
      { key: 'feas_a', label: 'A', text: '4 / 5' },
    ])
  })

  it('marks an item overdue past its status’ max. days', () => {
    const s = setup()
    // In Prototype / PoC (84 days) since 1 January; today is 27 November.
    expect(s.model.cards.value.get(PROJECT)!.overdue).toBe(true)
    expect(s.model.cards.value.get(PROJECT)!.color).toBe('var(--pgpm-note-blue)')
  })

  it('counts a project’s milestones, done of those not cancelled', () => {
    const m = (id: string, status: 'done' | 'planned' | 'cancelled') => ({
      id,
      item_id: PROJECT,
      kind: 'milestone' as const,
      title: id,
      status,
      sort_order: id,
    })
    const s = setup({ milestones: [m('a', 'done'), m('b', 'planned'), m('c', 'cancelled')] })
    expect(s.model.cards.value.get(PROJECT)!.progress).toEqual({ done: 1, total: 2 })
  })

  it('keeps a card model while nothing it shows changes', () => {
    const s = setup()
    const before = s.model.cards.value.get(IDEA)
    s.editor.move(new Map([[C1, { x: 10, y: 10 }]]))
    s.source.put('items', { ...s.source.items.value.find((i) => i.id === PROJECT)!, title: 'Renamed' })
    expect(s.model.cards.value.get(IDEA)).toBe(before)
    expect(s.model.cards.value.get(PROJECT)!.title).toBe('Renamed')
  })

  it('direct helpers', () => {
    expect(itemKey({ type: 'lead', number: null })).toBe('L-…')
    expect(cssColor('#ABCDEF')).toBe('#ABCDEF')
    expect(cssColor('red')).toBeNull()
    expect(itemColor({ type: 'note', color: null })).toBe('var(--pgpm-note-yellow)')
    expect(
      dodProgress([
        { id: '1', milestone_id: 'm', text: 'a', state: 'done', sort_order: 'a' },
        { id: '2', milestone_id: 'm', text: 'b', state: 'na', sort_order: 'b' },
        { id: '3', milestone_id: 'm', text: 'c', state: 'open', sort_order: 'c' },
      ]),
    ).toEqual({ done: 1, total: 2 })
    expect(milestoneTone({ kind: 'gate', status: 'planned', decision: 'no_go' })).toBe('no_go')
    expect(milestoneTone({ kind: 'milestone', status: 'in_progress', decision: null })).toBe('active')
    const now = Date.parse('2026-11-27T12:00:00')
    expect(milestoneOverdue({ due_date: '2026-11-26', status: 'planned' }, now)).toBe(true)
    expect(milestoneOverdue({ due_date: '2026-11-26', status: 'done' }, now)).toBe(false)
    expect(milestoneOverdue({ due_date: '2026-11-27', status: 'planned' }, now)).toBe(false)
  })
})

describe('keyboard', () => {
  const key = (k: string, mods: Partial<KeyboardEvent> = {}) => commandFor({ key: k, ...mods })

  it('maps the shortcuts of doc 01 §12.2 and §12.6', () => {
    expect(key('Delete')).toBe('delete')
    expect(key('Backspace')).toBe('delete')
    expect(key('z', { ctrlKey: true })).toBe('undo')
    expect(key('Z', { metaKey: true, shiftKey: true })).toBe('redo')
    expect(key('y', { ctrlKey: true })).toBe('redo')
    expect(key('c', { metaKey: true })).toBe('copy')
    expect(key('v', { ctrlKey: true })).toBe('paste')
    expect(key('d', { ctrlKey: true })).toBe('duplicate')
    expect(key(']', { ctrlKey: true })).toBe('forward')
    expect(key('}', { ctrlKey: true, shiftKey: true })).toBe('toFront')
    expect(commandFor({ key: '8', code: 'BracketLeft', metaKey: true })).toBe('backward')
    expect(key('[', { ctrlKey: true, shiftKey: true })).toBe('toBack')
    expect(key('f')).toBe('fit')
    expect(key('Escape')).toBe('escape')
    expect(key('x')).toBeNull()
  })

  it('leaves keys typed into a field alone', () => {
    const input = document.createElement('input')
    expect(commandFor({ key: 'Delete', target: input })).toBeNull()
  })
})
