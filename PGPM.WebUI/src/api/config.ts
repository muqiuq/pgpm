import type { StatusCategory } from '@/local/rows'
import { fetchJson } from './client'

/**
 * Statuses administration (docs/01 §5, *Configuration: Manage*, online): the endpoints change the configuration, the
 * result reaches every device through the sync (the rows are `statuses` [S, ro]). Until openapi.json describes them
 * they go through `fetchJson`; the integration switches them to the generated client.
 */
export interface StatusInput {
  itemType: 'idea' | 'lead' | 'project'
  name: string
  color: string
  icon: string | null
  category: StatusCategory
  isDefault: boolean
  maxDays: number | null
}

export const statusesApi = {
  create: (body: StatusInput) => fetchJson<unknown>('/api/v1/config/statuses', { method: 'POST', body }),
  update: (id: string, body: Omit<StatusInput, 'itemType'> & { itemType?: StatusInput['itemType'] }) =>
    fetchJson<unknown>(`/api/v1/config/statuses/${id}`, { method: 'PUT', body }),
  archive: (id: string) => fetchJson<unknown>(`/api/v1/config/statuses/${id}/archive`, { method: 'POST' }),
  unarchive: (id: string) => fetchJson<unknown>(`/api/v1/config/statuses/${id}/unarchive`, { method: 'POST' }),
  order: (itemType: StatusInput['itemType'], ids: string[]) =>
    fetchJson<unknown>('/api/v1/config/statuses/order', { method: 'PUT', body: { itemType, ids } }),
}
