import { call, client, type Schemas } from './client'

export type SystemStatusItem = Schemas['SystemStatusItem']
export type SystemClientItem = Schemas['SystemClientItem']
export type SystemTaskItem = Schemas['SystemTaskItem']

export const systemKeys = {
  status: ['system', 'status'] as const,
}

/**
 * Administration → System (docs/03 §9, *System: View*): the instance, storage, the sync's devices and live connections,
 * the background tasks' health. `resetClient` deletes a device's row: it starts over at its next sync (a live one at once).
 */
export const systemApi = {
  status: () => call(client.GET('/api/v1/system')),
  resetClient: (clientId: string) =>
    call(client.DELETE('/api/v1/system/clients/{id}', { params: { path: { id: clientId } } })),
}
