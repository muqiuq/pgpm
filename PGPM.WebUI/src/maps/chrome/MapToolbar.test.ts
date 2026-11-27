import { afterEach, describe, expect, it } from 'vitest'
import { mountWithUi } from '@/test/mount'
import { CANVAS_TOOLS } from '../tools'
import MapToolbar from './MapToolbar.vue'

describe('the map toolbar from the keyboard', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('moves between its buttons with the arrows, Home and End', async () => {
    const w = mountWithUi(MapToolbar, {
      props: {
        tool: 'select',
        tools: CANVAS_TOOLS,
        canEdit: true,
        canUndo: false,
        canRedo: false,
        undoLabel: null,
        redoLabel: null,
        layersOpen: false,
      },
      attachTo: document.body,
    })
    const buttons = () => [...(w.element as HTMLElement).querySelectorAll<HTMLButtonElement>('button:not([disabled])')]
    const first = buttons()[0]!
    first.focus()
    await w.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(buttons()[1])
    await w.trigger('keydown', { key: 'End' })
    expect(document.activeElement).toBe(buttons().at(-1))
    await w.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(first)
    await w.trigger('keydown', { key: 'ArrowUp' })
    expect(document.activeElement).toBe(buttons().at(-1))
    await w.trigger('keydown', { key: 'Home' })
    expect(document.activeElement).toBe(first)
    w.unmount()
  })
})
