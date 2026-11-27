import { call, client, type Schemas } from './client'

export type AppInfo = Schemas['AppInfo']

/** What the server is (anonymous): its name and version. */
export const appApi = {
  info: () => call(client.GET('/api/v1/info')),
}
