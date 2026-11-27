import type { StatusCategory } from '@/local/rows'
import { call, client, type Schemas } from './client'

/**
 * Statuses administration (docs/01 §5, docs/03 §3.3, *Configuration: Manage*, online): the endpoints change the
 * configuration, the result reaches every device through the sync (the rows are `statuses` [S, ro]). Errors: 400
 * `validation_failed` with `errors` by field; 409 `duplicate_name`, `default_required`, `status_archived` (on update) and
 * `default_status` (archiving the default) with a `detail` for the person.
 */
export interface StatusInput {
  itemType: 'idea' | 'lead' | 'project'
  name: string
  color: string
  /** A PrimeIcons name as stored: `pi-eye`. */
  icon: string | null
  category: StatusCategory
  isDefault: boolean
  maxDays: number | null
}

export type StatusItem = Schemas['StatusItem']

export const statusesApi = {
  create: (body: StatusInput) => call<StatusItem>(client.POST('/api/v1/config/statuses', { body })),
  /** `itemType` may be sent, but never changes. */
  update: (id: string, body: Omit<StatusInput, 'itemType'> & { itemType?: StatusInput['itemType'] }) =>
    call<StatusItem>(client.PUT('/api/v1/config/statuses/{id}', { params: { path: { id } }, body })),
  archive: (id: string) =>
    call<StatusItem>(client.POST('/api/v1/config/statuses/{id}/archive', { params: { path: { id } } })),
  unarchive: (id: string) =>
    call<StatusItem>(client.POST('/api/v1/config/statuses/{id}/unarchive', { params: { path: { id } } })),
  /** Every id of the type in the new order (sort orders 10, 20, …): the type's statuses in that order. */
  order: (itemType: StatusInput['itemType'], ids: string[]) =>
    call<StatusItem[]>(client.PUT('/api/v1/config/statuses/order', { body: { itemType, ids } })),
}
