import { call, client, type Schemas } from './client'
import type { Area, Level } from './rights'
import type { ListQuery } from '@/composables/useServerTable'

export type UserItem = Schemas['UserItem']
export type CreatedUser = Schemas['CreatedUser']
export type TemporaryPassword = Schemas['TemporaryPassword']
export type GroupItem = Schemas['GroupItem']
export type GroupDetail = Schemas['GroupDetail']
export type AuditEntryItem = Schemas['AuditEntryItem']
export type AuditEventInfo = Schemas['AuditEventInfo']
export type AuditQuery = ListQuery & {
  event?: string
  prefix?: string
  actor?: Schemas['AuditActor']
  outcome?: Schemas['AuditOutcome']
  userId?: string
  subjectId?: string
  from?: string
  to?: string
}
export type Rights = Partial<Record<Area, Level>>

/** The query keys of the online-only admin lists (vue-query). */
export const adminKeys = {
  users: ['users'] as const,
  groups: ['groups'] as const,
  rights: ['rights'] as const,
  audit: ['audit'] as const,
}

const id = (value: string) => ({ params: { path: { id: value } } })

/** Users, groups and rights (admins only) and the audit log (Audit log: View), docs/03 §4.5. */
export const adminApi = {
  users: {
    list: (query: ListQuery & { groupId?: string }) => call(client.GET('/api/v1/users', { params: { query } })),
    get: (userId: string) => call(client.GET('/api/v1/users/{id}', id(userId))),
    access: (userId: string) => call(client.GET('/api/v1/users/{id}/access', id(userId))),
    create: (body: {
      username: string
      displayName?: string | null
      email?: string | null
      isAdmin: boolean
      groupIds: string[]
    }) => call(client.POST('/api/v1/users', { body })),
    update: (
      userId: string,
      body: { displayName: string; email?: string | null; isAdmin?: boolean; isActive: boolean; groupIds?: string[] },
    ) => call(client.PUT('/api/v1/users/{id}', { ...id(userId), body })),
    remove: (userId: string) => call(client.DELETE('/api/v1/users/{id}', id(userId))),
    resetPassword: (userId: string) => call(client.POST('/api/v1/users/{id}/reset-password', id(userId))),
    resetTotp: (userId: string) => call(client.POST('/api/v1/users/{id}/reset-totp', id(userId))),
    unlock: (userId: string) => call(client.POST('/api/v1/users/{id}/unlock', id(userId))),
    linkMicrosoft: (userId: string, objectId: string, tenantId: string | null) =>
      call(client.PUT('/api/v1/users/{id}/microsoft-account', { ...id(userId), body: { objectId, tenantId } })),
    unlinkMicrosoft: (userId: string) => call(client.DELETE('/api/v1/users/{id}/microsoft-account', id(userId))),
  },
  groups: {
    list: () => call(client.GET('/api/v1/groups')),
    get: (groupId: string) => call(client.GET('/api/v1/groups/{id}', id(groupId))),
    create: (body: { name: string; description?: string | null; entraAppRole?: string | null; rights: Rights }) =>
      call(client.POST('/api/v1/groups', { body: body as Schemas['GroupRequest'] })),
    update: (
      groupId: string,
      body: { name: string; description?: string | null; entraAppRole?: string | null; rights?: Rights },
    ) => call(client.PUT('/api/v1/groups/{id}', { ...id(groupId), body: body as Schemas['GroupRequest'] })),
    rights: (groupId: string, rights: Rights) =>
      call(
        client.PUT('/api/v1/groups/{id}/rights', { ...id(groupId), body: { rights } as Schemas['GroupRightsRequest'] }),
      ),
    members: (groupId: string, userIds: string[]) =>
      call(client.PUT('/api/v1/groups/{id}/members', { ...id(groupId), body: { userIds } })),
    remove: (groupId: string) => call(client.DELETE('/api/v1/groups/{id}', id(groupId))),
  },
  audit: {
    list: (query: AuditQuery) => call(client.GET('/api/v1/audit', { params: { query } })),
    events: () => call(client.GET('/api/v1/audit/events')),
  },
}
