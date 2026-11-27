import { describe, expect, it } from 'vitest'
import { compareKeyThenId } from '@/domain/fractional'
import { parseClipboard } from './clipboard'
import { BASE, C1, C2, E1, IDEA, L1, MAP_ID, P1, R1, setup, T1, TOP } from './testFixtures'

const order = (s: ReturnType<typeof setup>, layer = BASE) =>
  s.source.elements.value
    .filter((e) => e.layer_id === layer)
    .sort((a, b) => compareKeyThenId(a.z, a.id, b.z, b.id))
    .map((e) => e.id)

describe('moving and resizing', () => {
  it('writes one patch per element, rounded, in one batch', () => {
    const s = setup()
    s.editor.move(
      new Map([
        [C1, { x: 10.123, y: 20.456 }],
        [R1, { x: 5, y: 305 }],
      ]),
    )
    const log = s.source.log.value
    expect(log).toHaveLength(2)
    expect(log.map((m) => m.op)).toEqual(['patch', 'patch'])
    expect(log[0]!.batch).toBe(log[1]!.batch)
    expect(log[0]!.changes).toEqual({ x: { old: 0, new: 10.12 }, y: { old: 0, new: 20.46 } })
    expect(s.row(C1)).toMatchObject({ x: 10.12, y: 20.46 })
  })

  it('writes nothing for a move that changes nothing', () => {
    const s = setup()
    s.editor.move(new Map([[C1, { x: 0, y: 0 }]]))
    expect(s.source.log.value).toHaveLength(0)
    expect(s.editor.history.canUndo.value).toBe(false)
  })

  it('sets a frame', () => {
    const s = setup()
    s.editor.setFrame(R1, { x: 0, y: 300, width: 150.555, height: 75 })
    expect(s.row(R1)).toMatchObject({ width: 150.56, height: 75 })
  })
})

describe('undo and redo', () => {
  it('undoes and redoes a move with inverse patches', () => {
    const s = setup()
    s.editor.move(new Map([[C1, { x: 50, y: 60 }]]))
    expect(s.editor.history.undoLabel.value).toBe('Move')
    expect(s.editor.undo()).toEqual([C1])
    expect(s.row(C1)).toMatchObject({ x: 0, y: 0 })
    expect(s.source.log.value.at(-1)!.changes).toEqual({ x: { old: 50, new: 0 }, y: { old: 60, new: 0 } })
    expect(s.editor.history.canRedo.value).toBe(true)
    s.editor.redo()
    expect(s.row(C1)).toMatchObject({ x: 50, y: 60 })
    expect(s.editor.history.canRedo.value).toBe(false)
  })

  it('a new operation clears the redo stack', () => {
    const s = setup()
    s.editor.move(new Map([[C1, { x: 50, y: 60 }]]))
    s.editor.undo()
    s.editor.setText(R1, 'New')
    expect(s.editor.history.canRedo.value).toBe(false)
    expect(s.editor.redo()).toBeNull()
  })

  it('undoes an added element by removing it, and redo adds it again under a new id', () => {
    const s = setup()
    const id = s.editor.add({ kind: 'rect', x: 1, y: 2, width: 30, height: 40 })!
    expect(s.row(id)).toMatchObject({ kind: 'rect', layer_id: TOP, map_id: MAP_ID })
    s.editor.undo()
    expect(s.row(id)).toBeUndefined()
    const [again] = s.editor.redo()!
    expect(again).not.toBe(id)
    expect(s.row(again!)).toMatchObject({ kind: 'rect', x: 1, y: 2, width: 30, height: 40 })
    // Later steps follow the new id.
    s.editor.move(new Map([[again!, { x: 9, y: 9 }]]))
    s.editor.undo()
    expect(s.row(again!)).toMatchObject({ x: 1, y: 2 })
  })

  it('undoes a delete: the element comes back under a new id and its line attaches to it again', () => {
    const s = setup()
    s.editor.remove([C1])
    expect(s.row(C1)).toBeUndefined()
    const line = s.row(L1)!
    expect(line.start_element_id).toBeNull()
    expect(line.end_element_id).toBe(R1)
    const touched = s.editor.undo()!
    const restored = s.source.elements.value.find((e) => e.item_id === IDEA && e.x === 0 && e.y === 0)!
    expect(restored.id).not.toBe(C1)
    expect(touched).toContain(restored.id)
    expect(s.row(L1)!.start_element_id).toBe(restored.id)
    // Inserted before the line's patch refers to it.
    const tail = s.source.log.value.slice(-2)
    expect(tail.map((m) => m.op)).toEqual(['insert', 'patch'])
    s.editor.redo()
    expect(s.row(restored.id)).toBeUndefined()
    expect(s.row(L1)!.start_element_id).toBeNull()
  })

  it('writes nothing to undo with read access', () => {
    const s = setup()
    s.editor.move(new Map([[C1, { x: 50, y: 60 }]]))
    s.source.setAccess('read')
    expect(s.editor.undo()).toBeNull()
    expect(s.row(C1)).toMatchObject({ x: 50 })
  })
})

describe('z order', () => {
  it('brings to front and sends to back within the layer, writing only the moved rows', () => {
    const s = setup()
    expect(order(s)).toEqual([C1, C2, R1, E1, L1, P1])
    s.editor.reorder('toFront', [C1])
    expect(order(s)).toEqual([C2, R1, E1, L1, P1, C1])
    expect(s.source.log.value).toHaveLength(1)
    expect(Object.keys(s.source.log.value[0]!.changes!)).toEqual(['z'])
    s.editor.reorder('toBack', [P1])
    expect(order(s)).toEqual([P1, C2, R1, E1, L1, C1])
  })

  it('moves forward and backward one place', () => {
    const s = setup()
    s.editor.reorder('forward', [C2])
    expect(order(s)).toEqual([C1, R1, C2, E1, L1, P1])
    s.editor.reorder('backward', [E1])
    expect(order(s)).toEqual([C1, R1, E1, C2, L1, P1])
    s.editor.undo()
    expect(order(s)).toEqual([C1, R1, C2, E1, L1, P1])
  })

  it('writes nothing for the front element brought to front', () => {
    const s = setup()
    s.editor.reorder('toFront', [P1])
    expect(s.source.log.value).toHaveLength(0)
  })

  it('moves elements to another layer, on top of it', () => {
    const s = setup()
    s.editor.moveToLayer([R1, C1], TOP)
    expect(order(s, TOP)).toEqual([T1, C1, R1])
    expect(s.row(C1)!.layer_id).toBe(TOP)
  })
})

describe('deleting', () => {
  it('removes elements, never items, and frees the lines attached to them where they are drawn', () => {
    const s = setup()
    const before = s.model.lineOf(s.row(L1)!)
    s.editor.remove([R1])
    expect(s.row(R1)).toBeUndefined()
    const line = s.row(L1)!
    expect(line.end_element_id).toBeNull()
    expect(line.start_element_id).toBe(C1)
    const after = s.model.lineOf(line)
    expect(after.end.x).toBeCloseTo(before.end.x, 1)
    expect(after.end.y).toBeCloseTo(before.end.y, 1)
    expect(s.source.items.value).toHaveLength(3)
  })

  it('deletes a layer moving its elements down, or with them', () => {
    const s = setup()
    s.editor.deleteLayer(TOP, 'move')
    expect(s.source.layers.value.map((l) => l.id)).toEqual([BASE])
    expect(s.row(T1)!.layer_id).toBe(BASE)
    expect(order(s).at(-1)).toBe(T1)
    s.editor.undo()
    expect(s.source.layers.value).toHaveLength(2)
    const s2 = setup()
    s2.editor.deleteLayer(TOP, 'delete')
    expect(s2.row(T1)).toBeUndefined()
  })

  it('never deletes the last layer', () => {
    const s = setup()
    s.editor.deleteLayer(TOP, 'delete')
    s.editor.deleteLayer(BASE, 'delete')
    expect(s.source.layers.value).toHaveLength(1)
  })
})

describe('clipboard', () => {
  it('pastes new elements with new ids; cards stay cards of the same items', () => {
    const s = setup()
    const text = s.editor.copy([C1, R1, L1])!
    const data = parseClipboard(text)!
    expect(data.mapId).toBe(MAP_ID)
    expect(data.elements.map((e) => e.id)).toEqual([C1, R1, L1])
    const ids = s.editor.paste(text)
    expect(ids).toHaveLength(3)
    for (const id of ids) expect([C1, R1, L1]).not.toContain(id)
    const [card, rect, line] = ids.map((id) => s.row(id)!)
    expect(card).toMatchObject({ kind: 'item', item_id: IDEA, x: 20, y: 20, layer_id: TOP })
    expect(rect).toMatchObject({ kind: 'rect', x: 20, y: 320, text: 'Box' })
    // The copied line connects the copies.
    expect(line).toMatchObject({ start_element_id: card!.id, end_element_id: rect!.id })
    expect(s.model.cardsOfItem.value.get(IDEA)).toHaveLength(3)
  })

  it('a line copied without its element gets a free end where it was drawn', () => {
    const s = setup()
    const drawn = s.model.lineOf(s.row(L1)!)
    const [id] = s.editor.paste(s.editor.copy([L1])!)
    const line = s.row(id!)!
    expect(line.start_element_id).toBeNull()
    expect(line.end_element_id).toBeNull()
    const pasted = s.model.lineOf(line)
    expect(pasted.start.x).toBeCloseTo(drawn.start.x + 20, 1)
    expect(pasted.end.y).toBeCloseTo(drawn.end.y + 20, 1)
  })

  it('pastes from another map centred where asked, and ignores other text', () => {
    const s = setup()
    const other = s.editor.copy([R1])!.replace(MAP_ID, 'another')
    const [id] = s.editor.paste(other, { x: 1000, y: 1000 })
    expect(s.row(id!)).toMatchObject({ x: 950, y: 975 })
    expect(s.editor.paste('just text')).toEqual([])
  })

  it('duplicates in one undoable step', () => {
    const s = setup()
    const ids = s.editor.duplicate([E1, C2])
    expect(ids).toHaveLength(2)
    s.editor.undo()
    for (const id of ids) expect(s.row(id)).toBeUndefined()
  })
})

describe('styles, texts, connectors', () => {
  it('sets style members as dotted keys', () => {
    const s = setup()
    s.editor.setStyle([R1, E1], { fill: '#fde68a', strokeWidth: 3 })
    expect(s.row(R1)!.style).toEqual({ fill: '#fde68a', strokeWidth: 3 })
    expect(s.source.log.value[0]!.changes).toEqual({
      'style.fill': { old: undefined, new: '#fde68a' },
      'style.strokeWidth': { old: undefined, new: 3 },
    })
  })

  it('connects a line end to an element and frees it again', () => {
    const s = setup()
    s.editor.setLineEnd(L1, 'end', { x: 450, y: 330 }, E1)
    expect(s.row(L1)!.end_element_id).toBe(E1)
    const g = s.model.lineOf(s.row(L1)!)
    // On the ellipse's border, facing the card.
    const dx = (g.end.x - 450) / 50
    const dy = (g.end.y - 330) / 30
    expect(Math.hypot(dx, dy)).toBeCloseTo(1, 3)
    s.editor.setLineEnd(L1, 'end', { x: 700, y: 700 }, null)
    expect(s.row(L1)!.end_element_id).toBeNull()
    expect(s.model.lineOf(s.row(L1)!).end).toEqual({ x: 700, y: 700 })
  })
})

describe('read access', () => {
  it('blocks every write', () => {
    const s = setup({ access: 'read' })
    expect(s.editor.canEdit.value).toBe(false)
    s.editor.move(new Map([[C1, { x: 5, y: 5 }]]))
    s.editor.remove([C1])
    s.editor.reorder('toFront', [C1])
    s.editor.setStyle([R1], { fill: 'pink' })
    expect(s.editor.add({ kind: 'rect', x: 0, y: 0, width: 10, height: 10 })).toBeNull()
    expect(s.editor.paste(s.editor.copy([C1])!)).toEqual([])
    expect(s.editor.addLayer()).toBeNull()
    s.editor.patchMap({ name: 'x' })
    expect(s.source.log.value).toHaveLength(0)
    expect(s.errors).toHaveLength(0)
  })

  it('blocks writes in read-only mode even with edit access', () => {
    const s = setup({}, { readonly: true })
    s.editor.move(new Map([[C1, { x: 5, y: 5 }]]))
    expect(s.source.log.value).toHaveLength(0)
  })
})
