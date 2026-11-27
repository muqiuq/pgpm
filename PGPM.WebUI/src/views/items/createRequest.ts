import { ref } from 'vue'
import type { LocationQuery } from 'vue-router'
import { typeOfList } from '@/domain/itemTypes'
import type { ItemType } from '@/local/rows'

/**
 * *New note* / *New idea* from a link (`/notes?new=1` – the installed app's shortcuts, docs/03 §11.2): the router takes
 * `new` out of the URL (so the list's own URL state stays as it was) and leaves the request here; the type's list opens
 * its create dialog and clears it.
 */
export const createRequested = ref<ItemType | null>(null)

/** A list route with `?new=1`: the same route without it, the request noted. Null for any other route. */
export function takeCreateRequest(to: {
  name?: unknown
  path: string
  query: LocationQuery
  hash: string
}): { path: string; query: LocationQuery; hash: string; replace: true } | null {
  if (to.query.new !== '1' || typeof to.name !== 'string') return null
  const type = typeOfList(to.name)
  if (!type) return null
  createRequested.value = type
  const query = { ...to.query }
  delete query.new
  return { path: to.path, query, hash: to.hash, replace: true }
}
