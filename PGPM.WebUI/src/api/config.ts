import type { StatusCategory } from '@/local/rows'
import { call, client, fetchApi, type Schemas } from './client'

/**
 * Configuration administration (docs/01 §5, §6, §10.2, §13; docs/03 §3.3, §17; *Configuration: Manage*, online): the
 * endpoints change the configuration, the result reaches every device through the sync (the rows are [S, ro]), so the
 * admin pages read the synced tables and call `engine.syncNow()` after each change. Everything uses the generated
 * client (openapi.json describes it).
 *
 * Statuses: errors 400 `validation_failed` with `errors` by field; 409 `duplicate_name`, `default_required`,
 * `status_archived` (on update) and `default_status` (archiving the default) with a `detail` for the person.
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

export type SectionItem = Schemas['SectionItem']
export type SectionAccessItem = Schemas['SectionAccessItem']
export type FieldItem = Schemas['FieldItem']
export type FieldUsage = Schemas['FieldUsage']
export type TemplateDetail = Schemas['TemplateDetail']
export type RoleItem = Schemas['RoleItem']
export type PackageInfo = Schemas['PackageInfo']
export type PackageDiff = Schemas['PackageDiff']
export type PackageDiffEntry = Schemas['PackageDiffEntry']
export type PackageApplyResult = Schemas['PackageApplyResult']

export type SectionLevel = 'view' | 'edit'

/** `POST`/`PUT /api/v1/config/sections`: `access` null on PUT leaves the groups unchanged. */
export interface SectionInput {
  name: string
  description: string | null
  appliesTo: ('idea' | 'project')[]
  defaultFor: ('idea' | 'project')[]
  collapsedByDefault: boolean
  access: { groupId: string; level: SectionLevel }[] | null
}

/** `POST`/`PUT /api/v1/config/fields`: `key` and `type` are taken on create only (immutable). */
export interface FieldInput {
  sectionId: string
  key?: string
  label: string
  helpText: string | null
  type?: Schemas['FieldType']
  config: Record<string, unknown>
  required: boolean
  defaultValue: unknown
}

export interface TemplateMilestoneInput {
  /** An existing template milestone's id: updated; none: created. Missing ones are tombstoned. */
  id?: string | null
  kind: 'milestone' | 'gate'
  title: string
  description: string | null
  offsetDays: number | null
  dod: string[]
  onGoStatusId: string | null
  onNoGoStatusId: string | null
}

/** `POST`/`PUT /api/v1/config/templates`: every list complete, in order. */
export interface TemplateInput {
  name: string
  description: string | null
  color: string | null
  icon: string | null
  defaultStatusId: string | null
  statusIds: string[]
  sectionIds: string[]
  fieldDefaults: { fieldId: string; value: unknown }[]
  defaultShares: { groupId: string; access: 'read' | 'edit' | 'manage' }[]
  milestones: TemplateMilestoneInput[]
}

export interface RoleInput {
  name: string
  description: string | null
  sortOrder?: number | null
}

/** A package request (docs/03 §15 #46): an uploaded package or a built-in one, and the conflicts to overwrite. */
export type PackageSource = { package: Record<string, unknown> } | { builtIn: string }

const id = (value: string) => ({ params: { path: { id: value } } })
type Json = Schemas['JsonObject']

export const sectionsApi = {
  create: (body: SectionInput) => call(client.POST('/api/v1/config/sections', { body })),
  update: (sectionId: string, body: SectionInput) =>
    call(client.PUT('/api/v1/config/sections/{id}', { ...id(sectionId), body })),
  archive: (sectionId: string) => call(client.POST('/api/v1/config/sections/{id}/archive', id(sectionId))),
  unarchive: (sectionId: string) => call(client.POST('/api/v1/config/sections/{id}/unarchive', id(sectionId))),
  order: (ids: string[]) => call(client.PUT('/api/v1/config/sections/order', { body: { ids } })),
  /** The sections with their groups (`section_groups` is server-only: the synced rows don't say who may see them). */
  list: () => call(client.GET('/api/v1/config/sections')),
  get: (sectionId: string) => call(client.GET('/api/v1/config/sections/{id}', id(sectionId))),
}

export const fieldsApi = {
  create: (body: FieldInput) =>
    call(client.POST('/api/v1/config/fields', { body: body as unknown as Schemas['FieldRequest'] })),
  update: (fieldId: string, body: FieldInput) =>
    call(
      client.PUT('/api/v1/config/fields/{id}', { ...id(fieldId), body: body as unknown as Schemas['FieldRequest'] }),
    ),
  archive: (fieldId: string) => call(client.POST('/api/v1/config/fields/{id}/archive', id(fieldId))),
  unarchive: (fieldId: string) => call(client.POST('/api/v1/config/fields/{id}/unarchive', id(fieldId))),
  usage: (fieldId: string) => call(client.GET('/api/v1/config/fields/{id}/usage', id(fieldId))),
  /** `confirm`: the number of values the usage reported (409 `confirmation_required` otherwise). */
  remove: (fieldId: string, confirm: number) =>
    call(client.DELETE('/api/v1/config/fields/{id}', { params: { path: { id: fieldId }, query: { confirm } } })),
  order: (sectionId: string, ids: string[]) =>
    call(client.PUT('/api/v1/config/fields/order', { body: { sectionId, ids } })),
}

export const templatesApi = {
  create: (body: TemplateInput) =>
    call(client.POST('/api/v1/config/templates', { body: body as unknown as Schemas['TemplateRequest'] })),
  update: (templateId: string, body: TemplateInput) =>
    call(
      client.PUT('/api/v1/config/templates/{id}', {
        ...id(templateId),
        body: body as unknown as Schemas['TemplateRequest'],
      }),
    ),
  archive: (templateId: string) => call(client.POST('/api/v1/config/templates/{id}/archive', id(templateId))),
  unarchive: (templateId: string) => call(client.POST('/api/v1/config/templates/{id}/unarchive', id(templateId))),
  order: (ids: string[]) => call(client.PUT('/api/v1/config/templates/order', { body: { ids } })),
}

export const rolesApi = {
  create: (body: RoleInput) => call(client.POST('/api/v1/config/roles', { body })),
  update: (roleId: string, body: RoleInput) => call(client.PUT('/api/v1/config/roles/{id}', { ...id(roleId), body })),
  remove: (roleId: string) => call(client.DELETE('/api/v1/config/roles/{id}', id(roleId))),
}

export const packagesApi = {
  list: () => call(client.GET('/api/v1/config/packages')),
  dryRun: (source: PackageSource, overwrite: string[] = []) =>
    call(
      client.POST('/api/v1/config/packages/dry-run', {
        body: { ...(source as Record<string, unknown>), overwrite } as Schemas['PackageRequest'],
      }),
    ),
  apply: (source: PackageSource, overwrite: string[] = []) =>
    call(
      client.POST('/api/v1/config/packages/apply', {
        body: { ...(source as Record<string, unknown>), overwrite } as Schemas['PackageRequest'],
      }),
    ),
  /** The package document (all configuration, or what `name` brought) as a file's text. */
  async export(name?: string): Promise<{ json: Json; text: string }> {
    const response = await fetchApi(
      `/api/v1/config/packages/export${name ? `?package=${encodeURIComponent(name)}` : ''}`,
    )
    const text = await response.text()
    return { json: JSON.parse(text) as Json, text }
  },
}
