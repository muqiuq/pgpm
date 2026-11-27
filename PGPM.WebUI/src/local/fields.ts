import { isEmptyValue, valueError } from '@/domain/fields/fieldWidget'
import { evaluateScore } from '@/domain/score'
import type { Catalogue } from './catalogue'
import type { PgpmDb } from './db'
import { inTransaction, type RepoTransaction } from './repo'
import {
  allowsAccess,
  type FieldDefinitionRow,
  type FieldValueRow,
  type ItemRow,
  type ItemSectionRow,
  type SectionItemType,
  type SectionRow,
  type Stored,
} from './rows'

/**
 * Sections and field values on ideas and projects (docs/01 §6.2, data model §7.5–§7.6), on the local data: which
 * sections an item shows (`item_sections`, in order), adding and removing them (values stay – hidden until the section
 * is back), one value per field (`field_values`: inserted once, then patched, so that a collision is a conflict of that
 * one field), and the scores computed from the values (never stored, docs/03 §16).
 */

const byOrder = (a: ItemSectionRow, b: ItemSectionRow) =>
  a.sortOrder - b.sortOrder || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)

/** An item's sections in their order. */
export function itemSectionsOf(db: PgpmDb, itemId: string): Promise<Stored<ItemSectionRow>[]> {
  return inTransaction(readSections(db, itemId))
}

async function readSections(db: PgpmDb, itemId: string): Promise<Stored<ItemSectionRow>[]> {
  const rows = (await db.synced('item_sections').where('itemId').equals(itemId).toArray()) as unknown as Stored<
    ItemSectionRow[]
  >
  return rows.filter((r) => !r.deleted).sort(byOrder)
}

/** An item's field values (one per field; a second row of a pair – two devices offline – loses to the older one). */
export function fieldValuesOf(db: PgpmDb, itemId: string): Promise<Stored<FieldValueRow>[]> {
  return inTransaction(readValues(db, itemId))
}

async function readValues(db: PgpmDb, itemId: string): Promise<Stored<FieldValueRow>[]> {
  const rows = (await db.synced('field_values').where('itemId').equals(itemId).toArray()) as unknown as Stored<
    FieldValueRow[]
  >
  return oneValuePerField(rows)
}

/**
 * The live values, one per item and field: the oldest row of a pair wins (the server merges the younger into it). Keyed
 * by both, so the rows of many items (the lists' field columns) keep each item's values – keyed by the field alone, only
 * the oldest item's survived.
 */
export function oneValuePerField<T extends FieldValueRow>(rows: readonly T[]): T[] {
  const byPair = new Map<string, T>()
  for (const row of rows) {
    if (row.deleted) continue
    const pair = `${row.itemId} ${row.fieldId}`
    const other = byPair.get(pair)
    if (!other || row.createdAt < other.createdAt || (row.createdAt === other.createdAt && row.id < other.id))
      byPair.set(pair, row)
  }
  return [...byPair.values()]
}

/** The values by field key, as score.ts reads them. */
export function valuesByKey(catalogue: Catalogue, values: readonly FieldValueRow[]): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const v of values) {
    const field = catalogue.fieldById.get(v.fieldId)
    if (field) out[field.key] = v.value
  }
  return out
}

/** A score field's result for an item's values (null: incomplete); null for any other field. */
export function scoreOf(
  catalogue: Catalogue,
  field: FieldDefinitionRow,
  values: Record<string, unknown>,
): number | null {
  if (field.type !== 'score') return null
  return evaluateScore(catalogue.defsByKey[field.key] ?? field, catalogue.defsByKey, values)
}

/** Every score's result for an item's values, by field id. */
export function scoresOf(catalogue: Catalogue, values: Record<string, unknown>): Map<string, number | null> {
  const out = new Map<string, number | null>()
  for (const field of catalogue.fields) if (field.type === 'score') out.set(field.id, scoreOf(catalogue, field, values))
  return out
}

/** The value a field shows on an item: a score's result, else the stored value (undefined: none). */
export function fieldValue(catalogue: Catalogue, field: FieldDefinitionRow, values: Record<string, unknown>): unknown {
  return field.type === 'score' ? scoreOf(catalogue, field, values) : values[field.key]
}

/** Whether a section can be on an item of the type: it applies to the type and isn't archived. */
export function sectionFits(section: SectionRow, type: ItemRow['type']): type is SectionItemType {
  return (type === 'idea' || type === 'project') && !section.isArchived && section.appliesTo.includes(type)
}

function requireEdit(item: ItemRow | undefined): asserts item is ItemRow {
  if (!item || item.deleted) throw new Error('The item is not on this device (any more).')
  if (!allowsAccess(item.access, 'edit')) throw new Error('You can read this item, not change it.')
}

/**
 * Adds a section to an item at the end (inside the caller's transaction), with the defaults of its fields that have no
 * value yet; answers false when the item has it already.
 */
export function addSection(
  tx: RepoTransaction,
  itemId: string,
  section: SectionRow,
  catalogue: Catalogue,
  defaults: ReadonlyMap<string, unknown> = new Map(),
): Promise<boolean> {
  return inTransaction(add(tx, itemId, section, catalogue, defaults))
}

async function add(
  tx: RepoTransaction,
  itemId: string,
  section: SectionRow,
  catalogue: Catalogue,
  defaults: ReadonlyMap<string, unknown>,
): Promise<boolean> {
  const item = (await tx.db.synced('items').get(itemId)) as Stored<ItemRow> | undefined
  requireEdit(item)
  if (!sectionFits(section, item.type)) throw new Error(`${section.name} doesn’t apply to ${item.type}s.`)
  const present = await itemSectionsOf(tx.db, itemId)
  if (present.some((s) => s.sectionId === section.id)) return false
  const last = present.length ? present[present.length - 1]!.sortOrder : 0
  await tx.create<ItemSectionRow>('item_sections', { itemId, sectionId: section.id, sortOrder: last + 10 })
  await fillDefaults(tx, itemId, [section], catalogue, defaults)
  return true
}

/**
 * The defaults of the sections' fields where the item has no value yet: a template's default, else the field's own
 * default value. Sections the user may not edit, scores and values that don't fit are left out (the server would refuse
 * them).
 */
export function fillDefaults(
  tx: RepoTransaction,
  itemId: string,
  sections: readonly SectionRow[],
  catalogue: Catalogue,
  defaults: ReadonlyMap<string, unknown> = new Map(),
): Promise<number> {
  return inTransaction(fill(tx, itemId, sections, catalogue, defaults))
}

async function fill(
  tx: RepoTransaction,
  itemId: string,
  sections: readonly SectionRow[],
  catalogue: Catalogue,
  defaults: ReadonlyMap<string, unknown>,
): Promise<number> {
  const existing = new Set((await fieldValuesOf(tx.db, itemId)).map((v) => v.fieldId))
  let count = 0
  for (const section of sections) {
    if (!section.canEdit) continue
    for (const field of catalogue.fieldsBySection.get(section.id) ?? []) {
      if (field.type === 'score' || field.isArchived || existing.has(field.id)) continue
      const value = defaults.has(field.id) ? defaults.get(field.id) : field.defaultValue
      if (isEmptyValue(value) || valueError({ ...field, required: false }, value)) continue
      await tx.create<FieldValueRow>('field_values', { itemId, fieldId: field.id, value })
      existing.add(field.id)
      count++
    }
  }
  return count
}

/** Removes a section from an item: its row is tombstoned, the values stay (docs/01 §6.2). */
export function removeSection(tx: RepoTransaction, itemSectionId: string): Promise<void> {
  return inTransaction(remove(tx, itemSectionId))
}

async function remove(tx: RepoTransaction, itemSectionId: string): Promise<void> {
  const row = (await tx.db.synced('item_sections').get(itemSectionId)) as Stored<ItemSectionRow> | undefined
  if (!row || row.deleted) return
  requireEdit((await tx.db.synced('items').get(row.itemId)) as Stored<ItemRow> | undefined)
  await tx.remove('item_sections', itemSectionId)
}

/** Why a field can't be changed here (read-only item, section not editable, a score), or null. */
export function fieldReadOnly(
  item: ItemRow,
  section: SectionRow | undefined,
  field: FieldDefinitionRow,
): string | null {
  if (field.type === 'score') return 'Computed from other fields.'
  if (item.deleted || !allowsAccess(item.access, 'edit')) return 'You can read this item, not change it.'
  if (section && !section.canEdit) return `Only some groups may edit ${section.name}.`
  return null
}

/**
 * Sets a field's value on an item (inside the caller's transaction): the existing row is patched (`value`), or a row is
 * inserted. The value is checked as the server does; null clears it. `old`: the value the edit started from (a Markdown
 * field's text when editing began), so that someone else's change meanwhile becomes a conflict.
 */
export function setFieldValue(
  tx: RepoTransaction,
  itemId: string,
  field: FieldDefinitionRow,
  value: unknown,
  options: { section?: SectionRow; old?: unknown } = {},
): Promise<'inserted' | 'patched' | 'unchanged'> {
  return inTransaction(set(tx, itemId, field, value, options))
}

async function set(
  tx: RepoTransaction,
  itemId: string,
  field: FieldDefinitionRow,
  value: unknown,
  options: { section?: SectionRow; old?: unknown },
): Promise<'inserted' | 'patched' | 'unchanged'> {
  const item = (await tx.db.synced('items').get(itemId)) as Stored<ItemRow> | undefined
  requireEdit(item)
  const readOnly = fieldReadOnly(item, options.section, field)
  if (readOnly) throw new Error(readOnly)
  const next = isEmptyValue(value) ? null : value
  const error = valueError(field, next)
  if (error) throw new FieldValueError(error)
  const existing = (await fieldValuesOf(tx.db, itemId)).find((v) => v.fieldId === field.id)
  if (existing) {
    const patchOptions = options.old !== undefined ? { old: { value: options.old } } : {}
    const before = JSON.stringify(existing.value ?? null)
    if (before === JSON.stringify(next) && options.old === undefined) return 'unchanged'
    await tx.patch('field_values', existing.id, { value: next }, patchOptions)
    return 'patched'
  }
  if (next === null) return 'unchanged'
  await tx.create<FieldValueRow>('field_values', { itemId, fieldId: field.id, value: next })
  return 'inserted'
}

/** A value the field refuses (shown under the editor). */
export class FieldValueError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'FieldValueError'
  }
}
