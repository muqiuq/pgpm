import { beforeEach, describe, expect, it } from 'vitest'
import { catalogue } from '@/test/catalogue'
import { holds, levelOrder, meets, setCatalogue } from './rights'

describe('the rights (docs/03 §6.1)', () => {
  beforeEach(() => setCatalogue(catalogue))

  it('takes the order of the levels from the server catalogue: a higher level includes the lower ones', () => {
    expect(levelOrder.Mcp).toEqual(['Use', 'View', 'Manage'])
    expect(holds(false, { Mcp: 'View' }, 'Mcp', 'Use')).toBe(true)
    expect(holds(false, { Mcp: 'View' }, 'Mcp', 'View')).toBe(true)
    expect(holds(false, { Mcp: 'View' }, 'Mcp', 'Manage')).toBe(false)
    expect(holds(false, { Projects: 'Create' }, 'Notes', 'Create')).toBe(false)
    expect(holds(false, { Projects: 'Create' }, 'Projects', 'Manage')).toBe(false)
    expect(holds(true, {}, 'System', 'View')).toBe(true)
  })

  it('counts only the very level while there is no catalogue', () => {
    setCatalogue([])
    expect(holds(false, { Mcp: 'Manage' }, 'Mcp', 'Manage')).toBe(true)
    expect(holds(false, { Mcp: 'Manage' }, 'Mcp', 'Use')).toBe(false)
  })

  it('meets a right, any of several, or the admin flag', () => {
    const can = (area: string, level: string) => area === 'Notes' && level === 'Create'
    expect(meets(['Notes', 'Create'], false, can)).toBe(true)
    expect(
      meets(
        {
          anyOf: [
            ['Ideas', 'Create'],
            ['Notes', 'Create'],
          ],
        },
        false,
        can,
      ),
    ).toBe(true)
    expect(meets('admin', false, can)).toBe(false)
    expect(meets('admin', true, can)).toBe(true)
  })
})
