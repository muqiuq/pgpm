import { beforeAll, describe, expect, it } from 'vitest'
import { holds, meets, setCatalogue, type Area, type Level, type Right } from '@/api/rights'
import { routes } from '@/router'
import { catalogue } from '@/test/catalogue'
import { navigation, visibleNavigation } from './navigation'

describe('the menu (docs/01 §3)', () => {
  beforeAll(() => setCatalogue(catalogue))

  const names = (groups: ReturnType<typeof visibleNavigation>) => groups.flatMap((g) => g.items.map((i) => i.name))
  /** A user without the admin flag holding exactly these levels. */
  const holding = (rights: Partial<Record<Area, Level>>) => (r: Right) =>
    meets(r, false, (a, l) => holds(false, rights, a, l))

  it('has Work, Directory and a folded Administration, in this order, and every icon once', () => {
    expect(navigation.map((g) => [g.label, !!g.folded])).toEqual([
      ['Work', false],
      ['Directory', false],
      ['Administration', true],
    ])
    expect(navigation[0]!.items.map((i) => i.label)).toEqual(['Maps', 'Projects', 'Leads', 'Ideas', 'Notes', 'Trash'])
    expect(navigation[1]!.items.map((i) => i.label)).toEqual(['Persons'])
    expect(navigation[2]!.items.map((i) => i.label)).toEqual([
      'Users',
      'Groups & Rights',
      'MCP',
      'Statuses',
      'Sections & Fields',
      'Project Templates',
      'Configuration packages',
      'Microsoft integration',
      'Audit log',
      'System',
    ])
    const icons = navigation.flatMap((g) => g.items.map((i) => i.icon))
    expect(new Set(icons).size).toBe(icons.length)
    expect(icons.every((i) => /^pi pi-[a-z-]+$/.test(i))).toBe(true)
  })

  it('shows a user without rights the work and the directory, and drops the empty Administration', () => {
    const groups = visibleNavigation(holding({}))
    expect(groups.map((g) => g.label)).toEqual(['Work', 'Directory'])
    expect(names(groups)).toEqual(['maps', 'projects', 'leads', 'ideas', 'notes', 'trash', 'persons'])
  })

  it('opens the configuration pages with Configuration: Manage, and users and groups to admins only', () => {
    const configurator = names(visibleNavigation(holding({ Configuration: 'Manage' })))
    expect(configurator).toEqual(expect.arrayContaining(['statuses', 'sections', 'templates', 'packages']))
    expect(configurator).not.toContain('users')
    expect(configurator).not.toContain('microsoft')
    expect(names(visibleNavigation((r) => meets(r, true, () => true)))).toContain('users')
  })

  it('shows the MCP admin page from Mcp: View, not with Use alone', () => {
    expect(names(visibleNavigation(holding({ Mcp: 'Use' })))).not.toContain('admin-mcp')
    expect(names(visibleNavigation(holding({ Mcp: 'View' })))).toContain('admin-mcp')
    expect(names(visibleNavigation(holding({ Mcp: 'Manage' })))).toContain('admin-mcp')
  })

  it('has a route for every entry, with its name, path and right – a page, or the placeholder of its phase', () => {
    const built = [
      'users',
      'groups',
      'audit-log',
      'projects',
      'leads',
      'ideas',
      'notes',
      'trash',
      'persons',
      'statuses',
      'sections',
      'templates',
      'packages',
      'admin-mcp',
      'microsoft',
      'maps',
      'system',
    ]
    for (const item of navigation.flatMap((g) => g.items)) {
      const route = routes.find((r) => r.name === item.name)
      expect(route?.path, item.name).toBe(item.to)
      expect(route?.meta?.right, item.name).toEqual(item.right)
      if (built.includes(item.name)) expect(route?.meta?.phase, item.name).toBeUndefined()
      else expect(route?.meta?.phase, item.name).toBeGreaterThan(1)
    }
  })
})
