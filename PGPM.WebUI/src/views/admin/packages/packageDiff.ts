import type { PackageDiff, PackageDiffEntry } from '@/api/config'

/**
 * The dry run of a configuration package (docs/03 §17, `POST /api/v1/config/packages/dry-run`) as the page shows it,
 * pure so that it is tested without a page: the entries grouped by entity in the importer's order, the conflicts one
 * may overwrite (local edits win unless chosen), and what applying would do.
 */

export type DiffAction = 'create' | 'update' | 'unchanged' | 'conflict'

/**
 * The entities in the importer's dependency order, with their names on the page: a diff entry's `entity` is the
 * package collection's singular (PackageImporter `Labels`: `group`, `status`, … `matrix preset`).
 */
export const entityLabels: readonly { entity: string; label: string }[] = [
  { entity: 'group', label: 'Groups' },
  { entity: 'status', label: 'Statuses' },
  { entity: 'section', label: 'Sections' },
  { entity: 'field', label: 'Fields' },
  { entity: 'template', label: 'Project templates' },
  { entity: 'role', label: 'Assignment roles' },
  { entity: 'view', label: 'Saved views' },
  { entity: 'map template', label: 'Map templates' },
  { entity: 'matrix preset', label: 'Matrix presets' },
]

export const actionLabels: Record<DiffAction, string> = {
  create: 'New',
  update: 'Update',
  unchanged: 'Unchanged',
  conflict: 'Changed here',
}

export interface DiffGroup {
  entity: string
  label: string
  entries: PackageDiffEntry[]
  counts: Record<DiffAction, number>
}

const actions: readonly DiffAction[] = ['create', 'update', 'unchanged', 'conflict']

export function actionOf(entry: PackageDiffEntry): DiffAction {
  return (actions as readonly string[]).includes(entry.action) ? (entry.action as DiffAction) : 'unchanged'
}

/** The entries by entity (known ones in the importer's order, others after), each group by action, then key. */
export function groupDiff(diff: PackageDiff, options: { hideUnchanged?: boolean } = {}): DiffGroup[] {
  const rank = new Map(entityLabels.map((e, i) => [e.entity, i]))
  const byEntity = new Map<string, PackageDiffEntry[]>()
  for (const entry of diff.entries) {
    if (options.hideUnchanged && actionOf(entry) === 'unchanged') continue
    const list = byEntity.get(entry.entity)
    if (list) list.push(entry)
    else byEntity.set(entry.entity, [entry])
  }
  const order: Record<DiffAction, number> = { conflict: 0, create: 1, update: 2, unchanged: 3 }
  return [...byEntity.entries()]
    .sort(([a], [b]) => (rank.get(a) ?? Infinity) - (rank.get(b) ?? Infinity) || (a < b ? -1 : a > b ? 1 : 0))
    .map(([entity, entries]) => {
      const counts: Record<DiffAction, number> = { create: 0, update: 0, unchanged: 0, conflict: 0 }
      for (const e of entries) counts[actionOf(e)]++
      return {
        entity,
        label: entityLabels.find((e) => e.entity === entity)?.label ?? entity,
        entries: [...entries].sort(
          (a, b) => order[actionOf(a)] - order[actionOf(b)] || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0),
        ),
        counts,
      }
    })
}

/** The keys of the conflicts (entities changed here) – the ones an import can overwrite. */
export function conflictKeys(diff: PackageDiff): string[] {
  return diff.entries.filter((e) => actionOf(e) === 'conflict').map((e) => e.key)
}

/**
 * The overwrite selection kept valid for a (new) dry run: only keys that are conflicts there. A key that was chosen and
 * ran again with `overwrite` shows as `update`; it stays chosen, so that applying overwrites it.
 */
export function keepSelection(selected: readonly string[], diff: PackageDiff): string[] {
  const overwritable = new Set(
    diff.entries.filter((e) => actionOf(e) === 'conflict' || selected.includes(e.key)).map((e) => e.key),
  )
  return selected.filter((k) => overwritable.has(k))
}

export function toggleKey(selected: readonly string[], key: string, on: boolean): string[] {
  return on ? [...new Set([...selected, key])] : selected.filter((k) => k !== key)
}

/** What applying the package will do, in words: "3 new, 2 updated, 1 kept (changed here)". */
export function applySummary(diff: PackageDiff, overwrite: readonly string[]): string {
  const chosen = new Set(overwrite)
  let create = 0
  let update = 0
  let kept = 0
  for (const e of diff.entries) {
    const action = actionOf(e)
    if (action === 'create') create++
    else if (action === 'update') update++
    else if (action === 'conflict') {
      if (chosen.has(e.key)) update++
      else kept++
    }
  }
  if (!create && !update) return kept ? `Nothing to apply; ${kept} kept (changed here).` : 'Nothing to apply.'
  return [`${create} new`, `${update} updated`, kept ? `${kept} kept (changed here)` : ''].filter(Boolean).join(', ')
}

/** A package file's name, version and title as the upload shows them; an error message for anything else. */
export function readPackage(text: string):
  | { ok: true; json: Record<string, unknown>; name: string; version: string; title: string }
  | {
      ok: false
      error: string
    } {
  let json: unknown
  try {
    json = JSON.parse(text)
  } catch {
    return { ok: false, error: 'The file is not JSON.' }
  }
  if (typeof json !== 'object' || json === null || Array.isArray(json)) return { ok: false, error: 'Not a package.' }
  const p = json as Record<string, unknown>
  if (p.format !== 'pgpm-config-package') return { ok: false, error: 'Not a PGPM configuration package (format).' }
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  return { ok: true, json: p, name: str(p.name), version: str(p.version), title: str(p.title) || str(p.name) }
}
