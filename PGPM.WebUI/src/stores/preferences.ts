import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { darkClass } from '@/design/preset'

export type Theme = 'system' | 'light' | 'dark'
/** The Markdown editor's two modes (docs/03 §18, PNMTD docs/locations-knowledge-plan.md §5.4). */
export type EditorMode = 'visual' | 'markdown'
/** Descriptions and fields remember their mode apart (docs/03 §18.2: `page` and `field`). */
export type EditorModes = { page: EditorMode; field: EditorMode }

const storageKey = 'pgpm.preferences'

interface Stored {
  theme?: Theme
  drawerCollapsed?: boolean
  /** The drawer's groups folded (true) or opened (false) by hand; the others as they start. */
  navFolds?: Record<string, boolean>
  /** Column widths a person dragged, in px, per table and column (useColumnWidths). */
  tableWidths?: TableWidths
  /** The Markdown editor's last mode, per kind of text (docs/03 §18.2). */
  editorModes?: Partial<EditorModes>
}

export type TableWidths = Record<string, Record<string, number>>

function read(): Stored {
  try {
    return JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Stored
  } catch {
    return {}
  }
}

const isMode = (value: unknown): value is EditorMode => value === 'visual' || value === 'markdown'

/** Only tables of columns of finite numbers: whatever else was stored is dropped. */
function widthsOf(value: unknown): TableWidths {
  if (!value || typeof value !== 'object') return {}
  const out: TableWidths = {}
  for (const [table, columns] of Object.entries(value as Record<string, unknown>)) {
    if (!columns || typeof columns !== 'object') continue
    const kept = Object.entries(columns as Record<string, unknown>).filter(
      (e): e is [string, number] => typeof e[1] === 'number' && Number.isFinite(e[1]),
    )
    if (kept.length) out[table] = Object.fromEntries(kept)
  }
  return out
}

/**
 * Per-browser preferences (PNMTD, docs/03 §12.1): theme, the drawer as a rail and its folded groups, the widths of
 * columns a person dragged, and the Markdown editor's last mode for descriptions and for fields (docs/03 §18).
 */
export const usePreferences = defineStore('preferences', () => {
  const stored = read()
  const theme = ref<Theme>(stored.theme ?? 'system')
  const drawerCollapsed = ref(stored.drawerCollapsed ?? false)
  const navFolds = ref<Record<string, boolean>>(
    stored.navFolds && typeof stored.navFolds === 'object' ? { ...stored.navFolds } : {},
  )
  const modes = stored.editorModes ?? {}
  const editorModes = ref<EditorModes>({
    page: isMode(modes.page) ? modes.page : 'visual',
    field: isMode(modes.field) ? modes.field : 'visual',
  })
  const tableWidths = ref<TableWidths>(widthsOf(stored.tableWidths))
  const systemDark = ref(false)

  // A page can fold the drawer to its rail while it is open (the map canvas, the editing page): a temporary state,
  // never stored, and never the drawerCollapsed preference.
  const railHolds = ref(0)
  const railHeld = computed(() => railHolds.value > 0)

  /** Folds the drawer to its rail until the returned function is called. */
  function holdRail(): () => void {
    railHolds.value++
    let released = false
    return () => {
      if (!released) {
        released = true
        railHolds.value = Math.max(0, railHolds.value - 1)
      }
    }
  }

  const media =
    typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null
  if (media) {
    systemDark.value = media.matches
    media.addEventListener?.('change', (e) => (systemDark.value = e.matches))
  }

  function isDark() {
    return theme.value === 'dark' || (theme.value === 'system' && systemDark.value)
  }

  function apply() {
    document.documentElement.classList.toggle(darkClass, isDark())
  }

  watch([theme, systemDark], apply, { immediate: true })
  watch(
    [theme, drawerCollapsed, navFolds, tableWidths, editorModes],
    () =>
      localStorage.setItem(
        storageKey,
        JSON.stringify({
          theme: theme.value,
          drawerCollapsed: drawerCollapsed.value,
          navFolds: navFolds.value,
          tableWidths: tableWidths.value,
          editorModes: editorModes.value,
        }),
      ),
    { deep: true },
  )

  /** Light ↔ dark, starting from what is shown now. */
  function toggleTheme() {
    theme.value = isDark() ? 'light' : 'dark'
  }

  /** Whether a drawer group is folded: as the person left it, else as it starts. */
  function isFolded(group: string, foldedByDefault: boolean): boolean {
    return navFolds.value[group] ?? foldedByDefault
  }

  function toggleFold(group: string, foldedByDefault: boolean) {
    navFolds.value = { ...navFolds.value, [group]: !isFolded(group, foldedByDefault) }
  }

  return {
    theme,
    drawerCollapsed,
    navFolds,
    tableWidths,
    editorModes,
    railHeld,
    holdRail,
    isDark,
    toggleTheme,
    isFolded,
    toggleFold,
  }
})
