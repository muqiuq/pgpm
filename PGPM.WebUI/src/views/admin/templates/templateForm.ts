import type { TemplateInput } from '@/api/config'
import type { ShareSpec } from '@/api/sync'
import { valueError, type FieldLike } from '@/domain/fields/fieldWidget'
import type { TemplateParts } from '@/local/catalogue'
import type { MilestoneKind } from '@/local/rows'

/**
 * The project template editor's rules (docs/01 §10.2, data model §7.7, docs/03 §3.3), pure so that they are tested
 * without a page: the form from a synced template and its parts, the complete request (`TemplateInput`: every list
 * in order, existing milestones with their ids), the validation the page can do before the server, and how an offset
 * reads ("+8 weeks").
 */

export interface MilestoneForm {
  /** A key for the list (v-for), also for new ones. */
  uid: string
  /** The template milestone's id: updated on save; null: created. */
  id: string | null
  kind: MilestoneKind
  title: string
  description: string
  offsetDays: number | null
  dod: string[]
  onGoStatusId: string | null
  onNoGoStatusId: string | null
}

export interface TemplateForm {
  name: string
  description: string
  color: string | null
  icon: string
  defaultStatusId: string | null
  /** The allowed statuses; empty: all project statuses. */
  statusIds: string[]
  /** In order. */
  sectionIds: string[]
  fieldDefaults: { fieldId: string; value: unknown }[]
  defaultShares: ShareSpec[]
  milestones: MilestoneForm[]
}

let counter = 0
export const newUid = () => `m${++counter}`

export function emptyTemplateForm(): TemplateForm {
  return {
    name: '',
    description: '',
    color: null,
    icon: '',
    defaultStatusId: null,
    statusIds: [],
    sectionIds: [],
    fieldDefaults: [],
    defaultShares: [],
    milestones: [],
  }
}

export function newMilestone(kind: MilestoneKind, offsetDays: number | null = null): MilestoneForm {
  return {
    uid: newUid(),
    id: null,
    kind,
    title: '',
    description: '',
    offsetDays,
    dod: [],
    onGoStatusId: null,
    onNoGoStatusId: null,
  }
}

export function formFromParts(parts: TemplateParts): TemplateForm {
  const t = parts.template
  return {
    name: t.name,
    description: t.description ?? '',
    color: t.color,
    icon: t.icon ?? '',
    defaultStatusId: t.defaultStatusId,
    statusIds: [...parts.statusIds],
    sectionIds: parts.sections.map((s) => s.sectionId),
    fieldDefaults: parts.defaults.map((d) => ({ fieldId: d.fieldId, value: d.value })),
    defaultShares: (Array.isArray(t.defaultShares) ? t.defaultShares : []).map((s) => ({
      groupId: s.groupId,
      access: s.access,
    })),
    milestones: parts.milestones.map((m) => ({
      uid: newUid(),
      id: m.id,
      kind: m.kind,
      title: m.title,
      description: m.description ?? '',
      offsetDays: m.offsetDays,
      dod: Array.isArray(m.dod) ? [...m.dod] : [],
      onGoStatusId: m.onGoStatusId,
      onNoGoStatusId: m.onNoGoStatusId,
    })),
  }
}

/** `eye`, `pi-eye` and `pi pi-eye` are the same icon: stored as `pi-eye` (docs/03 §17). */
export function iconName(text: string): string | null {
  const bare = text
    .trim()
    .replace(/^pi\s+/, '')
    .replace(/^pi-/, '')
  return bare ? `pi-${bare}` : null
}

/** The request: trimmed texts, empty ones null, empty DoD lines dropped, status suggestions for gates only. */
export function templateInput(form: TemplateForm): TemplateInput {
  return {
    name: form.name.trim(),
    description: form.description.trim() || null,
    color: form.color,
    icon: iconName(form.icon),
    defaultStatusId: form.defaultStatusId,
    statusIds: [...form.statusIds],
    sectionIds: [...form.sectionIds],
    fieldDefaults: form.fieldDefaults.map((d) => ({ fieldId: d.fieldId, value: d.value })),
    defaultShares: form.defaultShares.map((s) => ({ groupId: s.groupId, access: s.access })),
    milestones: form.milestones.map((m) => ({
      ...(m.id ? { id: m.id } : {}),
      kind: m.kind,
      title: m.title.trim(),
      description: m.description.trim() || null,
      offsetDays: m.offsetDays,
      dod: m.dod.map((d) => d.trim()).filter(Boolean),
      onGoStatusId: m.kind === 'gate' ? m.onGoStatusId : null,
      onNoGoStatusId: m.kind === 'gate' ? m.onNoGoStatusId : null,
    })),
  }
}

export interface TemplateContext {
  /** The fields the defaults are for. */
  fieldById: ReadonlyMap<string, FieldLike & { sectionId: string }>
}

/** What the page can tell before the server: by member (`name`, `fieldDefaults.<fieldId>`, `milestones.<i>.title`, …). */
export function templateErrors(form: TemplateForm, ctx: TemplateContext): Record<string, string> {
  const errors: Record<string, string> = {}
  const name = form.name.trim()
  if (!name) errors.name = 'A name is needed.'
  else if (name.length > 100) errors.name = 'At most 100 characters.'
  if (form.color && !/^#[0-9a-f]{6}$/i.test(form.color)) errors.color = 'A colour #rrggbb.'
  const icon = iconName(form.icon)
  if (icon && !/^pi-[a-z0-9-]+$/.test(icon)) errors.icon = 'A PrimeIcons name such as pi-bolt.'
  const allowed = (id: string | null) => !id || !form.statusIds.length || form.statusIds.includes(id)
  if (!allowed(form.defaultStatusId)) errors.defaultStatusId = 'Not among the allowed statuses.'
  for (const d of form.fieldDefaults) {
    const field = ctx.fieldById.get(d.fieldId)
    if (!field) {
      errors[`fieldDefaults.${d.fieldId}`] = 'The field is gone.'
      continue
    }
    if (!form.sectionIds.includes(field.sectionId)) {
      errors[`fieldDefaults.${d.fieldId}`] = 'Its section is not part of the template.'
      continue
    }
    const problem = valueError({ ...field, required: true }, d.value)
    if (problem)
      errors[`fieldDefaults.${d.fieldId}`] = problem === 'The field is required.' ? 'A value is needed.' : problem
  }
  form.milestones.forEach((m, i) => {
    const title = m.title.trim()
    if (!title) errors[`milestones.${i}.title`] = 'A title is needed.'
    else if (title.length > 300) errors[`milestones.${i}.title`] = 'At most 300 characters.'
    if (m.offsetDays !== null && !Number.isInteger(m.offsetDays)) errors[`milestones.${i}.offsetDays`] = 'Whole days.'
    if (m.dod.some((d) => d.trim().length > 500)) errors[`milestones.${i}.dod`] = 'At most 500 characters each.'
    if (m.kind === 'gate') {
      if (!allowed(m.onGoStatusId)) errors[`milestones.${i}.onGoStatusId`] = 'Not among the allowed statuses.'
      if (!allowed(m.onNoGoStatusId)) errors[`milestones.${i}.onNoGoStatusId`] = 'Not among the allowed statuses.'
    }
  })
  return errors
}

/** An offset from the project start: "At the start", "+10 days", "+8 weeks", "−3 days". */
export function offsetText(days: number | null): string {
  if (days === null) return 'No due date'
  if (days === 0) return 'At the start'
  const sign = days < 0 ? '−' : '+'
  const n = Math.abs(days)
  if (n % 7 === 0) return `${sign}${n / 7} ${n === 7 ? 'week' : 'weeks'}`
  return `${sign}${n} ${n === 1 ? 'day' : 'days'}`
}
