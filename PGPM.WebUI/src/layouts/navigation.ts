import type { Right } from '@/api/rights'

export interface NavItem {
  label: string
  icon: string
  to: string
  /** The route's name (router/index.ts has one per entry). */
  name: string
  /** What its page needs (docs/03 §6.2); without, every signed-in user's. */
  right?: Right
}

export interface NavGroup {
  label: string
  /** Starts folded (Administration). Each browser remembers what was folded or opened. */
  folded?: boolean
  items: NavItem[]
}

/** Configuration: Manage – statuses, sections and fields, templates, configuration packages (docs/03 §6.2). */
const configuration: Right = ['Configuration', 'Manage']

/**
 * The drawer (docs/01 §3): the work first, then the people directory, then the administration, folded. Entries the user
 * lacks the right for go, and a group without entries with them. Every icon is used once.
 */
export const navigation: NavGroup[] = [
  {
    label: 'Work',
    items: [
      { label: 'Maps', icon: 'pi pi-map', to: '/maps', name: 'maps' },
      { label: 'Projects', icon: 'pi pi-briefcase', to: '/projects', name: 'projects' },
      { label: 'Leads', icon: 'pi pi-flag', to: '/leads', name: 'leads' },
      { label: 'Ideas', icon: 'pi pi-lightbulb', to: '/ideas', name: 'ideas' },
      { label: 'Notes', icon: 'pi pi-file-edit', to: '/notes', name: 'notes' },
      { label: 'Trash', icon: 'pi pi-trash', to: '/trash', name: 'trash' },
    ],
  },
  {
    label: 'Directory',
    // Everybody reads the directory (docs/03 §15 #15); Persons: Edit only changes it.
    items: [{ label: 'Persons', icon: 'pi pi-address-book', to: '/persons', name: 'persons' }],
  },
  {
    label: 'Administration',
    folded: true,
    items: [
      // Users, groups and rights: admin only (docs/03 §6.2).
      { label: 'Users', icon: 'pi pi-users', to: '/admin/users', name: 'users', right: 'admin' },
      { label: 'Groups & Rights', icon: 'pi pi-sitemap', to: '/admin/groups', name: 'groups', right: 'admin' },
      { label: 'MCP', icon: 'pi pi-microchip-ai', to: '/admin/mcp', name: 'admin-mcp', right: ['Mcp', 'View'] },
      { label: 'Statuses', icon: 'pi pi-tag', to: '/admin/statuses', name: 'statuses', right: configuration },
      {
        label: 'Sections & Fields',
        icon: 'pi pi-list-check',
        to: '/admin/sections',
        name: 'sections',
        right: configuration,
      },
      {
        label: 'Project Templates',
        icon: 'pi pi-clone',
        to: '/admin/templates',
        name: 'templates',
        right: configuration,
      },
      {
        label: 'Configuration packages',
        icon: 'pi pi-box',
        to: '/admin/packages',
        name: 'packages',
        right: configuration,
      },
      {
        label: 'Microsoft integration',
        icon: 'pi pi-microsoft',
        to: '/admin/microsoft',
        name: 'microsoft',
        right: ['Directory', 'Manage'],
      },
      {
        label: 'Audit log',
        icon: 'pi pi-history',
        to: '/admin/audit-log',
        name: 'audit-log',
        right: ['AuditLog', 'View'],
      },
      { label: 'System', icon: 'pi pi-server', to: '/admin/system', name: 'system', right: ['System', 'View'] },
    ],
  },
]

/** The drawer for a user: the entries they have the right for, and only the groups that keep one. */
export function visibleNavigation(allows: (right: Right) => boolean): NavGroup[] {
  return navigation
    .map((g) => ({ ...g, items: g.items.filter((i) => !i.right || allows(i.right)) }))
    .filter((g) => g.items.length > 0)
}
