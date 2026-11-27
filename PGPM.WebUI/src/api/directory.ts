import { call, client, type Schemas } from './client'

export type DirectoryStatusItem = Schemas['DirectoryStatusItem']
export type DirectoryRunItem = Schemas['DirectoryRunItem']

export const directoryKeys = {
  status: ['directory', 'status'] as const,
}

/**
 * Administration → Microsoft integration (docs/03 §8): the SSO's settings (read-only), the directory sync's last run and
 * *Run now* (*Directory: Manage*). A run is the background task's: `sync` answers 202 at once, the status follows.
 */
export const directoryApi = {
  status: () => call(client.GET('/api/v1/directory/status')),
  /** `full`: a full run, which also makes persons the directory no longer has inactive. 409 directory_off. */
  sync: (full: boolean) => call(client.POST('/api/v1/directory/sync', { params: { query: { full } } })),
}
