// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { formatFocus, parseFocus } from './focus'

describe('a focus in the URL (from PNMTD)', () => {
  const id = '0193f3c2-7d1e-7c55-9a35-8e2f1a6b4c90'
  const kinds = ['item', 'element'] as const

  it("takes the map's kinds and a UUID, in any case", () => {
    expect(parseFocus(`ITEM:${id.toUpperCase()}`, kinds)).toEqual({ kind: 'item', id })
    expect(parseFocus(` element:${id} `, kinds)).toEqual({ kind: 'element', id })
    expect(parseFocus(formatFocus('item', id.toUpperCase()), kinds)).toEqual({ kind: 'item', id })
  })

  it('refuses anything else', () => {
    expect(parseFocus('item:42', kinds)).toBeNull()
    expect(parseFocus(`layer:${id}`, kinds)).toBeNull()
    expect(parseFocus(`item:${id}x`, kinds)).toBeNull()
    expect(parseFocus(`item:${id.replaceAll('-', '')}`, kinds)).toBeNull()
    expect(parseFocus(42, kinds)).toBeNull()
    expect(parseFocus(['item', id], kinds)).toBeNull()
    expect(parseFocus('item:', kinds)).toBeNull()
  })
})
