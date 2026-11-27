import type { Area, Level } from '@/api/rights'
import type { ItemType } from '@/local/rows'

/**
 * What the views need to know of each item type (docs/01 §7–§10): names, routes, icons, the right that creates one and
 * whether it has a status. The routes are named `<plural>` (list), `<type>` (page with tabs) and `<type>-edit` (the
 * description's editing page): `notes`, `note`, `note-edit`, …
 */
export interface ItemTypeInfo {
  type: ItemType
  /** "note" */
  label: string
  /** "Note" */
  title: string
  /** "Notes" */
  plural: string
  /** The list's route name and path segment: `notes` → `/notes`. */
  list: string
  /** The page's route name. */
  page: string
  /** The editing page's route name. */
  edit: string
  icon: string
  /** The right that creates one (docs/03 §6.2). */
  create: readonly [Area, Level]
  /** Ideas, leads and projects have statuses (docs/01 §5). */
  hasStatus: boolean
  /** What the description is called: *Content* for notes. */
  descriptionLabel: string
  /** Converted into a project (docs/01 §8, §9). */
  convertible: boolean
}

function info(
  type: ItemType,
  title: string,
  plural: string,
  icon: string,
  area: Area,
  extra: Partial<ItemTypeInfo> = {},
): ItemTypeInfo {
  const list = plural.toLowerCase()
  return {
    type,
    label: title.toLowerCase(),
    title,
    plural,
    list,
    page: type,
    edit: `${type}-edit`,
    icon,
    create: [area, 'Create'],
    hasStatus: type !== 'note',
    descriptionLabel: 'Description',
    convertible: false,
    ...extra,
  }
}

export const itemTypes: Record<ItemType, ItemTypeInfo> = {
  note: info('note', 'Note', 'Notes', 'pi pi-file-edit', 'Notes', { descriptionLabel: 'Content' }),
  idea: info('idea', 'Idea', 'Ideas', 'pi pi-lightbulb', 'Ideas', { convertible: true }),
  lead: info('lead', 'Lead', 'Leads', 'pi pi-flag', 'Leads', { convertible: true }),
  project: info('project', 'Project', 'Projects', 'pi pi-briefcase', 'Projects'),
}

export const itemTypeList: readonly ItemType[] = ['note', 'idea', 'lead', 'project']

export type ItemTab = 'overview' | 'milestones' | 'links' | 'maps' | 'history'

/** An item page's tabs (docs/01 §4.4), linkable as `/<list>/:id/<tab>`: milestones for projects only. */
export function itemTabs(type: ItemType): ItemTab[] {
  return type === 'project'
    ? ['overview', 'milestones', 'links', 'maps', 'history']
    : ['overview', 'links', 'maps', 'history']
}

/** The type a route path segment names (`ideas` → idea); undefined for any other. */
export function typeOfList(segment: string): ItemType | undefined {
  return itemTypeList.find((t) => itemTypes[t].list === segment)
}

/** An item's page as a router location. */
export function itemRoute(item: { type: ItemType; id: string }, tab?: string) {
  return { name: itemTypes[item.type].page, params: tab ? { id: item.id, tab } : { id: item.id } }
}

/** An item's page as a path (`/ideas/<id>`): for Markdown links (docs/03 §18.3). */
export function itemPath(item: { type: ItemType; id: string }): string {
  return `/${itemTypes[item.type].list}/${item.id}`
}

/** The 12 preset colours of the item palette (docs/01 §4.1), on the note colours of PNMTD's design. */
export const presetColors: readonly { name: string; value: string }[] = [
  { name: 'Slate', value: '#64748b' },
  { name: 'Red', value: '#dc2626' },
  { name: 'Orange', value: '#ea580c' },
  { name: 'Amber', value: '#d97706' },
  { name: 'Yellow', value: '#eab308' },
  { name: 'Lime', value: '#65a30d' },
  { name: 'Green', value: '#16a34a' },
  { name: 'Teal', value: '#0d9488' },
  { name: 'Sky', value: '#0284c7' },
  { name: 'Indigo', value: '#4f46e5' },
  { name: 'Purple', value: '#9333ea' },
  { name: 'Pink', value: '#db2777' },
]
