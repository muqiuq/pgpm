import type { RouteLocationRaw } from 'vue-router'
import { normalise } from '@/composables/useLocalTable'
import { itemRoute, itemTypes } from '@/domain/itemTypes'
import type { PgpmDb } from './db'
import { itemKey, type ItemRow, type PersonRow } from './rows'

/**
 * The global search (docs/01 §3): every item type and the persons, in the local data only – instant and offline. The
 * query is normalised (lower case, no diacritics) and every word must occur; items rank by where it is found: the key
 * exactly, then a title that starts with it, a title that contains it, the description last. Persons are found by name
 * and e-mail, active ones only (inactive persons are hidden in pickers, docs/01 §16).
 */
export interface SearchResult {
  kind: 'item' | 'person'
  id: string
  icon: string
  /** The item's key (`I-42`). */
  key?: string
  title: string
  subtitle: string
  to: RouteLocationRaw
}

export interface SearchOptions {
  /** At most this many results per kind (default 8). */
  limit?: number
}

/** How well an item matches (lower is better), or null when not every word occurs. */
export function itemRank(
  item: Pick<ItemRow, 'type' | 'number' | 'title' | 'description'>,
  query: string,
): number | null {
  const words = normalise(query.trim()).split(/\s+/).filter(Boolean)
  if (!words.length) return null
  const key = normalise(itemKey(item))
  const title = normalise(item.title ?? '')
  const all = `${key} ${title} ${normalise(item.description ?? '')}`
  if (!words.every((w) => all.includes(w))) return null
  const phrase = words.join(' ')
  if (key === phrase || (item.number !== null && words.length === 1 && String(item.number) === phrase)) return 0
  if (title.startsWith(phrase)) return 1
  if (title.includes(phrase)) return 2
  if (words.every((w) => title.includes(w) || key.includes(w))) return 3
  return 4
}

/** How well a person matches (lower is better), or null. */
export function personRank(person: Pick<PersonRow, 'displayName' | 'email'>, query: string): number | null {
  const words = normalise(query.trim()).split(/\s+/).filter(Boolean)
  if (!words.length) return null
  const name = normalise(person.displayName ?? '')
  const email = normalise(person.email ?? '')
  if (!words.every((w) => name.includes(w) || email.includes(w))) return null
  const phrase = words.join(' ')
  if (name.startsWith(phrase) || email.startsWith(phrase)) return 1
  return 2
}

function snippet(text: string | null | undefined): string {
  const line = (text ?? '')
    .split('\n')
    .map((l) => l.replace(/^[#>*\-\s]+/, '').trim())
    .find(Boolean)
  return line ? (line.length > 80 ? `${line.slice(0, 80)}…` : line) : ''
}

/** The items and persons the query finds, best first: items, then persons. */
export async function searchLocal(db: PgpmDb, query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
  const limit = options.limit ?? 8
  if (!query.trim()) return []
  const [items, persons] = await Promise.all([
    db.synced('items').toArray() as unknown as Promise<ItemRow[]>,
    db.synced('persons').toArray() as unknown as Promise<PersonRow[]>,
  ])
  const foundItems = items
    .filter((i) => !i.deleted && itemTypes[i.type])
    .map((i) => ({ i, rank: itemRank(i, query) }))
    .filter((r): r is { i: ItemRow; rank: number } => r.rank !== null)
    .sort(
      (a, b) =>
        a.rank - b.rank ||
        String(b.i.updatedAt ?? '').localeCompare(String(a.i.updatedAt ?? '')) ||
        a.i.title.localeCompare(b.i.title),
    )
    .slice(0, limit)
    .map(({ i }): SearchResult => ({
      kind: 'item',
      id: i.id,
      icon: itemTypes[i.type].icon,
      key: itemKey(i),
      title: i.title,
      subtitle: snippet(i.description) || itemTypes[i.type].title,
      to: itemRoute(i),
    }))
  const foundPersons = persons
    .filter((p) => !p.deleted && p.isActive !== false)
    .map((p) => ({ p, rank: personRank(p, query) }))
    .filter((r): r is { p: PersonRow; rank: number } => r.rank !== null)
    .sort((a, b) => a.rank - b.rank || a.p.displayName.localeCompare(b.p.displayName))
    .slice(0, limit)
    .map(({ p }): SearchResult => ({
      kind: 'person',
      id: p.id,
      icon: 'pi pi-user',
      title: p.displayName,
      subtitle: [p.email, p.jobTitle, p.department].filter(Boolean).join(' · '),
      to: { name: 'person', params: { id: p.id } },
    }))
  return [...foundItems, ...foundPersons]
}
