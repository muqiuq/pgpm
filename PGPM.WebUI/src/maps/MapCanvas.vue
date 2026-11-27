<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, toRef, useSlots, watch } from 'vue'
import { useNow } from '@/composables/useNow'
import { jsonEqual } from '@/local/outbox'
import { cssColor } from './cards'
import SceneLayer from './elements/SceneLayer.vue'
import LayerPanel from './chrome/LayerPanel.vue'
import MapFilterBar from './chrome/MapFilterBar.vue'
import MapSidePanel from './chrome/MapSidePanel.vue'
import MapToolbar from './chrome/MapToolbar.vue'
import MiniMap from './chrome/MiniMap.vue'
import ZoomControls from './chrome/ZoomControls.vue'
import {
  CULL_MIN_ELEMENTS,
  detailAt,
  FAR_ZOOM,
  LOD_ZOOM,
  nextCull,
  nextDetail,
  SETTLE_MS,
  type CullState,
  type Detail,
} from './culling'
import type { MapDataSource } from './dataSource'
import { createMapEditor } from './editor'
import { colorKey, visibleIds, type FilterContext } from './filter'
import { parseFocus } from './focus'
import { useFileUrls } from './fileUrls'
import {
  bandBox,
  bandMode,
  boundsOf,
  boxOf,
  boxesOverlap,
  centreOf,
  connectorEnds,
  edgePath,
  padBox,
  rectOf,
  selectInBand,
  snapPoint,
  type Box,
  type Point,
  type Rect,
  type Shape,
} from './geometry'
import { commandFor, isTyping } from './keyboard'
import { DEFAULT_QUADRANT_LABELS, type Quadrant } from './matrixLayout'
import { isMatrixCard } from './matrixItems'
import { createMapModel, type VirtualLayer } from './model'
import { useMoving } from './moving'
import type { Option, PanelContent, SceneLookups } from './sceneTypes'
import { fitTo, MAX_ZOOM, MIN_ZOOM, usePanZoom, visibleBox, zoomAround, type View } from './panZoom'
import { HANDLE_CURSORS, HANDLES, handlePoint, resizeFrame, type Handle } from './resize'
import {
  frameOf,
  lineGeometry,
  lineRow,
  round2,
  shapeOf,
  storedEnds,
  topmostAt,
  type LineEnd,
  type LineGeometry,
} from './scene'
import {
  CANVAS_TOOLS,
  isItemTool,
  isPlacingTool,
  SIZES,
  type ImageRequest,
  type ItemRequest,
  type PlaceRequest,
  type Tool,
} from './tools'
import type { ElementRow, LayerRow, MapFilter, MapViewSettings } from './types'

/**
 * A canvas map (doc 01 §12.2–§12.7, docs/03 §13): the SVG world group from panZoom with the visible layers and their
 * elements, the selection with its handles, the tools, the layer panel, the filter bar, the minimap, the zoom controls
 * and the side panel – everything over a MapDataSource. Every completed operation writes at once (the editor); during
 * a drag only Vue state changes. Matrix maps use it with their cards as a virtual layer (MatrixMap.vue).
 *
 * The host answers the placing tools' requests (`requestItem` → *Create new* / *Add existing*, …), fills the side
 * panel's quick edit (`#item-panel`) and opens an item's page (`openItem`).
 */
const props = withDefaults(
  defineProps<{
    source: MapDataSource
    /** `?focus=item:<id>` or `element:<id>`: selects the item's cards and fits the view to them. */
    focus?: string | null
    readonly?: boolean
    tools?: readonly Tool[]
    /** Elements without rows on a layer of their own below the others (a matrix map's cards). */
    virtual?: VirtualLayer | null
    /** Item id → quadrant (matrix maps): card badges, the filter. */
    quadrants?: ReadonlyMap<string, { quadrant: Quadrant; label: string }> | null
    /** What *Fit* shows at least (a matrix map's world). */
    world?: Box | null
    /** Virtual cards move animated. */
    animated?: boolean
    /** The level-of-detail zoom and the culling threshold (culling.ts); the playground's spike knobs. */
    tuning?: { lodZoom?: number; farZoom?: number; cullFrom?: number } | null
  }>(),
  {
    focus: null,
    readonly: false,
    tools: () => CANVAS_TOOLS,
    virtual: null,
    quadrants: null,
    world: null,
    animated: false,
    tuning: null,
  },
)

const emit = defineEmits<{
  requestItem: [request: ItemRequest]
  requestMilestone: [request: PlaceRequest]
  requestProject: [request: PlaceRequest]
  requestImage: [request: ImageRequest]
  openItem: [itemId: string]
  error: [error: unknown]
}>()
const slots = useSlots()

const now = useNow()
const model = createMapModel(props.source, { now, virtual: toRef(props, 'virtual') })
const editor = createMapEditor(props.source, model, {
  readonly: toRef(props, 'readonly'),
  onError: (error) => emit('error', error),
})
const canEdit = editor.canEdit

const root = ref<HTMLElement | null>(null)
const svg = ref<SVGSVGElement | null>(null)
const pan = usePanZoom(svg)
const size = ref({ width: 800, height: 600 })
let resizeObserver: ResizeObserver | null = null

function measure() {
  const rect = svg.value?.getBoundingClientRect()
  if (rect && rect.width > 0 && rect.height > 0) size.value = { width: rect.width, height: rect.height }
}

// --- personal view settings (user_settings map:<id>) ---------------------------------------------------------------

const settings = ref<MapViewSettings>({
  ...(props.source.viewSettings.value ?? props.source.map.value?.default_view ?? {}),
})
watch(props.source.viewSettings, (s) => {
  if (s && !jsonEqual({ ...s, viewport: undefined }, { ...settings.value, viewport: undefined })) {
    settings.value = { ...s }
  }
})
let saveTimer: ReturnType<typeof setTimeout> | undefined
let viewport: View | null = props.source.viewSettings.value?.viewport ?? null
function saveSettings() {
  clearTimeout(saveTimer)
  saveTimer = setTimeout(() => {
    void props.source.setViewSettings({ ...settings.value, viewport }).catch((e: unknown) => emit('error', e))
  }, 600)
}
function updateSettings(patch: Partial<MapViewSettings>) {
  settings.value = { ...settings.value, ...patch }
  saveSettings()
}
const filter = computed<MapFilter>(() => {
  const s = settings.value
  return {
    types: s.types ?? null,
    statusIds: s.statusIds ?? null,
    personIds: s.personIds ?? null,
    colors: s.colors ?? null,
    templateIds: s.templateIds ?? null,
    quadrants: s.quadrants ?? null,
    score: s.score ?? null,
  }
})
const showLinks = computed(() => settings.value.showLinks ?? false)
const showMinimap = computed(() => settings.value.showMinimap ?? true)
const layersOpen = ref(false)

// --- grid -----------------------------------------------------------------------------------------------------------

const localGrid = ref<boolean | null>(null)
const grid = computed(() => localGrid.value ?? model.settings.value.grid ?? false)
const snapOn = computed(() => canEdit.value && (model.settings.value.snap ?? false))
const gridSize = computed(() => Math.max(4, model.settings.value.gridSize ?? 20))
function toggleGrid() {
  if (canEdit.value) editor.patchMap({ 'settings.grid': !grid.value }, grid.value ? 'Hide grid' : 'Show grid')
  else localGrid.value = !grid.value
}
function toggleSnap() {
  editor.patchMap({ 'settings.snap': !snapOn.value }, snapOn.value ? 'Snap off' : 'Snap on')
}

// --- what is drawn --------------------------------------------------------------------------------------------------

const hiddenLocally = computed(() => new Set(settings.value.hiddenLayerIds ?? []))
const shownLayers = computed(() => model.layers.value.filter((l) => l.is_visible && !hiddenLocally.value.has(l.id)))
const lockedLayers = computed(() => new Set(model.layers.value.filter((l) => l.is_locked).map((l) => l.id)))
/** The elements of the shown layers (virtual ones first), in drawing order. */
const candidates = computed(() => {
  const out: ElementRow[] = [...(props.virtual?.elements ?? [])]
  for (const l of shownLayers.value) out.push(...(model.elementsByLayer.value.get(l.id) ?? []))
  return out
})
const filterContext = computed<FilterContext>(() => ({
  itemsById: model.itemsById.value,
  personsByItem: model.personsByItem.value,
  quadrantOf: (id) => props.quadrants?.get(id)?.quadrant ?? null,
  valueOf: model.valueOf,
}))
const visible = computed(() => visibleIds(candidates.value, filter.value, filterContext.value))

const selection = ref<string[]>([])
const selectedSet = computed(() => new Set(selection.value))

// Moves and resizes in progress (or written and not yet back from the data source): Vue state only.
const moved = ref(new Map<string, Point>())
const resized = ref(new Map<string, Rect>())
const baseFrames = new WeakMap<ElementRow, Rect>()

/** Every element's frame: its move or resize in progress, else its row's (the same object while the row is). */
const frames = computed(() => {
  const out = new Map<string, Rect>()
  for (const el of model.elementsById.value.values()) {
    const r = resized.value.get(el.id)
    const m = moved.value.get(el.id)
    if (r) out.set(el.id, r)
    else if (m) out.set(el.id, { x: m.x, y: m.y, width: el.width, height: el.height })
    else {
      let f = baseFrames.get(el)
      if (!f) baseFrames.set(el, (f = frameOf(el)))
      out.set(el.id, f)
    }
  }
  return out
})

function shapeAt(id: string): Shape | null {
  const el = model.elementsById.value.get(id)
  const f = frames.value.get(id)
  if (!el || !f || el.kind === 'line') return null
  return shapeOf(el, f, el.milestone_id ? model.milestonesById.value.get(el.milestone_id) : undefined)
}

/** A line end being dragged: to a point, or onto an element. */
interface LineDraft {
  lineId: string | null
  end: 'start' | 'end'
  from: Point
  to: Point
  startTarget: string | null
  target: string | null
}
const lineDraft = shallowRef<LineDraft | null>(null)

const lineCache = new Map<string, { inputs: unknown[]; geometry: LineGeometry }>()
/** Each line's geometry, its connected ends on the elements' frames now; the same object while nothing it uses changed. */
const lines = computed(() => {
  const out = new Map<string, LineGeometry>()
  const f = frames.value
  const draft = lineDraft.value
  for (const el of model.elementsById.value.values()) {
    if (el.kind !== 'line') continue
    const mine = draft?.lineId === el.id ? draft : null
    const inputs = [
      el,
      f.get(el.id),
      el.start_element_id ? f.get(el.start_element_id) : null,
      el.end_element_id ? f.get(el.end_element_id) : null,
      mine?.to,
      mine?.target,
      mine?.end,
    ]
    const cached = lineCache.get(el.id)
    if (cached && cached.inputs.every((v, i) => v === inputs[i])) {
      out.set(el.id, cached.geometry)
      continue
    }
    const frame = f.get(el.id)!
    const row = frame.x !== el.x || frame.y !== el.y ? { ...el, x: frame.x, y: frame.y } : el
    const [a, b] = storedEnds(row)
    const endOf = (id: string | null | undefined, fallback: Point): LineEnd =>
      id ? (shapeAt(id) ?? fallback) : fallback
    let start = endOf(row.start_element_id, a)
    let end = endOf(row.end_element_id, b)
    if (mine) {
      const moving = mine.target ? (shapeAt(mine.target) ?? mine.to) : mine.to
      if (mine.end === 'start') start = moving
      else end = moving
    }
    const geometry = lineGeometry(start, end, el.style)
    lineCache.set(el.id, { inputs, geometry })
    out.set(el.id, geometry)
  }
  return out
})

function boxOfElement(el: ElementRow): Box {
  if (el.kind === 'line') {
    const g = lines.value.get(el.id)
    if (g) return g.box
  }
  return boxOf(frames.value.get(el.id) ?? frameOf(el))
}

// Level of detail and viewport culling (culling.ts): less detail and a new region at once where needed, more detail
// and a tighter region once the view has settled.
const culling = computed(() => candidates.value.length >= (props.tuning?.cullFrom ?? CULL_MIN_ELEMENTS))
const cullState = shallowRef<CullState | null>(null)
function targetDetail(): Detail {
  return detailAt(pan.view.value.k, props.tuning?.lodZoom ?? LOD_ZOOM, props.tuning?.farZoom ?? FAR_ZOOM)
}
const detail = ref<Detail>(targetDetail())
function visibleNow(): Box {
  return visibleBox(pan.view.value, size.value.width, size.value.height)
}
let settleViewTimer: ReturnType<typeof setTimeout> | undefined
function updateView(settled: boolean) {
  if (culling.value) cullState.value = nextCull(cullState.value, visibleNow(), pan.view.value.k, settled)
  detail.value = nextDetail(detail.value, targetDetail(), settled)
}
watch(
  [() => pan.view.value, culling, size],
  () => {
    updateView(false)
    clearTimeout(settleViewTimer)
    settleViewTimer = setTimeout(() => updateView(true), SETTLE_MS)
  },
  { immediate: true },
)

interface LayerList {
  layer: LayerRow
  elements: ElementRow[]
}
const layerLists = computed<LayerList[]>(() => {
  const region = culling.value ? (cullState.value?.region ?? null) : null
  const shown = visible.value
  const selected = selectedSet.value
  const keep = (el: ElementRow) =>
    shown.has(el.id) && (!region || selected.has(el.id) || boxesOverlap(boxOfElement(el), region))
  const lists: LayerList[] = []
  if (props.virtual) lists.push({ layer: props.virtual.layer, elements: props.virtual.elements.filter(keep) })
  for (const l of shownLayers.value) {
    lists.push({ layer: l, elements: (model.elementsByLayer.value.get(l.id) ?? []).filter(keep) })
  }
  return lists
})
const drawnCount = computed(() => layerLists.value.reduce((n, l) => n + l.elements.length, 0))

const fileIds = computed(() => {
  const ids = new Set<string>()
  for (const el of candidates.value) {
    if (el.kind === 'image' && el.file_id) ids.add(el.file_id)
    if (el.kind === 'item' && el.item_id && el.config?.size === 'expanded') {
      const cover = model.cards.value.get(el.item_id)?.coverFileId
      if (cover) ids.add(cover)
    }
  }
  return ids
})
const images = useFileUrls(props.source, fileIds)
const quadrantLabels = computed(() =>
  props.quadrants ? new Map([...props.quadrants].map(([id, q]) => [id, q.label])) : null,
)
const lookups = computed<SceneLookups>(() => ({
  cards: model.cards.value,
  items: model.itemsById.value,
  milestones: model.milestonesById.value,
  milestonesByProject: model.milestonesByProject.value,
  images: images.value,
  quadrants: quadrantLabels.value,
  now: now.value,
}))

/** *Show links*: a dashed connector per link between the nearest pair of the linked items' shown cards. */
const linkPaths = computed(() => {
  if (!showLinks.value) return []
  const cardsByItem = new Map<string, ElementRow[]>()
  for (const el of candidates.value) {
    if (el.kind !== 'item' || !el.item_id || !visible.value.has(el.id)) continue
    cardsByItem.set(el.item_id, [...(cardsByItem.get(el.item_id) ?? []), el])
  }
  const out: { id: string; d: string }[] = []
  for (const link of model.links.value) {
    const a = cardsByItem.get(link.from_item_id)
    const b = cardsByItem.get(link.to_item_id)
    if (!a || !b) continue
    let best: [Shape, Shape] | null = null
    let distance = Infinity
    for (const x of a) {
      for (const y of b) {
        const sx = shapeAt(x.id)
        const sy = shapeAt(y.id)
        if (!sx || !sy || x.id === y.id) continue
        const cx = centreOf(sx)
        const cy = centreOf(sy)
        const d = Math.hypot(cx.x - cy.x, cx.y - cy.y)
        if (d < distance) [best, distance] = [[sx, sy], d]
      }
    }
    if (best) {
      const ends = connectorEnds(best[0], best[1])
      out.push({ id: link.id, d: edgePath(ends.start, ends.end) })
    }
  }
  return out
})

// --- selection and the side panel ----------------------------------------------------------------------------------

const panelItem = ref<string | null>(null)
const panelMap = ref(false)
function select(ids: string[]) {
  selection.value = ids
  if (ids.length) {
    panelItem.value = null
    panelMap.value = false
  }
}
const panelContent = computed<PanelContent | null>(() => {
  if (selection.value.length) return { kind: 'elements', ids: selection.value }
  if (panelItem.value) return { kind: 'item', itemId: panelItem.value }
  return panelMap.value ? { kind: 'map' } : null
})
const panelOpen = computed(() => panelContent.value !== null)
// The panel keeps the last content while the drawer slides out (PNMTD's `shown`).
const shown = ref<PanelContent | null>(null)
watch(panelContent, (c) => c && (shown.value = c), { immediate: true })
function closePanel() {
  selection.value = []
  panelItem.value = null
  panelMap.value = false
}
// Elements that went away (deleted here or by someone else) leave the selection.
watch(model.elementsById, (byId) => {
  if (selection.value.some((id) => !byId.has(id))) selection.value = selection.value.filter((id) => byId.has(id))
})

function isMovable(id: string): boolean {
  const el = model.elementsById.value.get(id)
  if (!el || !canEdit.value || isMatrixCard(id) || lockedLayers.value.has(el.layer_id)) return false
  // A connected line follows its elements; it moves by its ends.
  return !(el.kind === 'line' && (el.start_element_id || el.end_element_id))
}

const selectedFrames = computed(() =>
  selection.value
    .map((id) => {
      const el = model.elementsById.value.get(id)
      return el && el.kind !== 'line' ? { id, frame: frames.value.get(id)! } : null
    })
    .filter((x): x is { id: string; frame: Rect } => x !== null),
)
const selectedLines = computed(() =>
  selection.value
    .map((id) => (model.elementsById.value.get(id)?.kind === 'line' ? { id, geometry: lines.value.get(id)! } : null))
    .filter((x): x is { id: string; geometry: LineGeometry } => !!x?.geometry),
)
/** One movable element that isn't a line: it gets resize handles. */
const resizable = computed(() => {
  if (selection.value.length !== 1 || !isMovable(selection.value[0]!)) return null
  return selectedFrames.value[0] ?? null
})
const lineEditable = computed(() => {
  if (selection.value.length !== 1 || !canEdit.value) return null
  const el = model.elementsById.value.get(selection.value[0]!)
  if (!el || el.kind !== 'line' || lockedLayers.value.has(el.layer_id)) return null
  return selectedLines.value[0] ?? null
})

// --- pointer gestures -----------------------------------------------------------------------------------------------

const tool = ref<Tool>('select')
const spaceHeld = ref(false)
const band = shallowRef<{ from: Point; to: Point } | null>(null)
const draft = shallowRef<{ tool: Tool; from: Point; to: Point } | null>(null)

type Gesture =
  | { kind: 'pan'; start: Point; background: boolean }
  | { kind: 'drag'; id: string; toggle: boolean; additive: boolean }
  | { kind: 'press'; id: string; toggle: boolean; additive: boolean }
  | { kind: 'band'; additive: boolean }
  | { kind: 'resize'; id: string; handle: Handle; start: Rect }
  | { kind: 'lineEnd' }
  | { kind: 'draw' }
let gesture: Gesture | null = null

const moving = useMoving(moved, {
  at: (id) => {
    const f = frames.value.get(id)
    return f ? { x: f.x, y: f.y } : { x: 0, y: 0 }
  },
  toWorld: (e) => pan.toWorld(e),
  snap: (p) => (snapOn.value ? snapPoint(p, gridSize.value) : p),
})

/** Overrides written to the data source: they go once the rows show them (or after a while, when a write failed). */
const settling = new Set<string>()
let settleTimer: ReturnType<typeof setTimeout> | undefined
function settle(ids: Iterable<string>) {
  for (const id of ids) settling.add(id)
  clearTimeout(settleTimer)
  settleTimer = setTimeout(() => {
    for (const id of settling) {
      dropOverride(id)
    }
    settling.clear()
  }, 3000)
  pruneSettled()
}
function dropOverride(id: string) {
  if (moved.value.has(id)) {
    const next = new Map(moved.value)
    next.delete(id)
    moved.value = next
  }
  if (resized.value.has(id)) {
    const next = new Map(resized.value)
    next.delete(id)
    resized.value = next
  }
}
function pruneSettled() {
  if (!settling.size || moving.isDragging() || gesture?.kind === 'resize') return
  for (const id of [...settling]) {
    const row = model.elementsById.value.get(id)
    const m = moved.value.get(id)
    const r = resized.value.get(id)
    const done =
      !row ||
      ((!m || (row.x === m.x && row.y === m.y)) &&
        (!r || (row.x === r.x && row.y === r.y && row.width === r.width && row.height === r.height)))
    if (done) {
      dropOverride(id)
      settling.delete(id)
    }
  }
}
watch(model.elementsById, pruneSettled)

/** The element (not a line, not a matrix card) a line end would attach to at this point. */
function targetAt(p: Point, except: string | null): string | null {
  const shapes: { id: string; shape: Shape }[] = []
  for (const list of layerLists.value) {
    for (const el of list.elements) {
      if (el.kind === 'line' || el.id === except || isMatrixCard(el.id)) continue
      const shape = shapeAt(el.id)
      if (shape) shapes.push({ id: el.id, shape })
    }
  }
  return topmostAt(shapes, p, 4 / pan.view.value.k)?.id ?? null
}

function elementIdOf(target: EventTarget | null): string | null {
  return (target as Element | null)?.closest?.('[data-element-id]')?.getAttribute('data-element-id') ?? null
}

function focusMap() {
  if (root.value && !editingId.value && document.activeElement !== root.value) root.value.focus({ preventScroll: true })
}

function onPointerDown(e: PointerEvent) {
  if (editingId.value) commitEdit()
  focusMap()
  const target = e.target as Element
  if (e.button === 1 || spaceHeld.value || tool.value === 'hand') {
    pan.onPointerDown(e)
    gesture = { kind: 'pan', start: { x: e.clientX, y: e.clientY }, background: false }
    return
  }
  if (e.button !== 0) return
  const p = pan.toWorld(e)
  const handle = target.closest?.('[data-handle]')?.getAttribute('data-handle') as Handle | null
  if (handle && resizable.value) {
    capture(e)
    gesture = { kind: 'resize', id: resizable.value.id, handle, start: { ...resizable.value.frame } }
    return
  }
  const lineEnd = target.closest?.('[data-line-end]')?.getAttribute('data-line-end') as 'start' | 'end' | null
  if (lineEnd && lineEditable.value) {
    capture(e)
    const g = lineEditable.value.geometry
    lineDraft.value = {
      lineId: lineEditable.value.id,
      end: lineEnd,
      from: lineEnd === 'start' ? g.end : g.start,
      to: p,
      startTarget: null,
      target: null,
    }
    gesture = { kind: 'lineEnd' }
    return
  }
  if (isPlacingTool(tool.value) && canEdit.value && editor.activeLayer.value) {
    capture(e)
    draft.value = { tool: tool.value, from: p, to: p }
    if (tool.value === 'line') {
      lineDraft.value = { lineId: null, end: 'end', from: p, to: p, startTarget: targetAt(p, null), target: null }
    }
    gesture = { kind: 'draw' }
    return
  }
  const id = elementIdOf(target)
  if (id && model.elementsById.value.has(id)) {
    pressElement(e, id)
    return
  }
  if (e.shiftKey || e.metaKey || e.ctrlKey) {
    capture(e)
    band.value = { from: p, to: p }
    gesture = { kind: 'band', additive: true }
    return
  }
  pan.onPointerDown(e)
  gesture = { kind: 'pan', start: { x: e.clientX, y: e.clientY }, background: true }
}

function capture(e: PointerEvent) {
  try {
    svg.value?.setPointerCapture(e.pointerId)
  } catch {
    // A synthetic pointer: the gesture works without capture.
  }
}

function pressElement(e: PointerEvent, id: string) {
  const additive = e.shiftKey || e.metaKey || e.ctrlKey
  const was = selectedSet.value.has(id)
  if (!was) select(additive ? [...selection.value, id] : [id])
  const toggle = additive && was
  const ids = (was || additive ? selection.value : [id]).filter(isMovable)
  if (tool.value === 'select' && ids.includes(id)) {
    // The pressed element first: the grid snaps its corner, the others keep their distance.
    moving.begin(e, [id, ...ids.filter((x) => x !== id)])
    gesture = { kind: 'drag', id, toggle, additive }
  } else {
    capture(e)
    gesture = { kind: 'press', id, toggle, additive }
  }
}

function onPointerMove(e: PointerEvent) {
  if (!gesture) return
  switch (gesture.kind) {
    case 'pan':
      pan.onPointerMove(e)
      break
    case 'drag':
      moving.move(e)
      break
    case 'band':
      band.value = { from: band.value!.from, to: pan.toWorld(e) }
      break
    case 'resize': {
      const next = resizeFrame(gesture.start, gesture.handle, pan.toWorld(e), {
        keepRatio: e.shiftKey,
        grid: snapOn.value ? gridSize.value : 0,
      })
      resized.value = new Map([[gesture.id, next]])
      break
    }
    case 'lineEnd': {
      const d = lineDraft.value!
      const p = pan.toWorld(e)
      lineDraft.value = { ...d, to: p, target: targetAt(p, d.lineId) }
      break
    }
    case 'draw': {
      const p = pan.toWorld(e)
      draft.value = { ...draft.value!, to: p }
      if (lineDraft.value) lineDraft.value = { ...lineDraft.value, to: p, target: targetAt(p, null) }
      break
    }
  }
}

function onPointerUp(e: PointerEvent) {
  const g = gesture
  gesture = null
  if (!g) {
    pan.onPointerUp(e)
    return
  }
  switch (g.kind) {
    case 'pan': {
      pan.onPointerUp(e)
      const still = Math.abs(e.clientX - g.start.x) + Math.abs(e.clientY - g.start.y) < 4
      if (g.background && still) closePanel()
      break
    }
    case 'drag': {
      const { click, ids } = moving.release()
      if (click) clickElement(g.id, g.toggle, g.additive)
      else if (ids.length) commitMoves(ids, 'Move')
      break
    }
    case 'press':
      clickElement(g.id, g.toggle, g.additive)
      break
    case 'band': {
      const b = band.value!
      band.value = null
      const box = bandBox(b.from, b.to)
      const rects = layerLists.value
        .filter((l) => !l.layer.is_locked)
        .flatMap((l) => l.elements.map((el) => ({ id: el.id, ...rectOf(boxOfElement(el)) })))
      const hit = selectInBand(box, rects, bandMode(b.from, b.to))
      select(g.additive ? [...new Set([...selection.value, ...hit])] : hit)
      break
    }
    case 'resize': {
      const r = resized.value.get(g.id)
      if (r) {
        const rounded = { x: round2(r.x), y: round2(r.y), width: round2(r.width), height: round2(r.height) }
        resized.value = new Map([[g.id, rounded]])
        editor.setFrame(g.id, rounded)
        settle([g.id])
      }
      break
    }
    case 'lineEnd': {
      const d = lineDraft.value!
      lineDraft.value = null
      if (d.lineId) editor.setLineEnd(d.lineId, d.end, d.to, d.target)
      break
    }
    case 'draw':
      finishDraw()
      break
  }
}

/** A click (not a drag): Shift/Cmd on a selected element takes it out; on one of several selected, that one alone. */
function clickElement(id: string, toggle: boolean, additive: boolean) {
  if (toggle) select(selection.value.filter((x) => x !== id))
  else if (!additive && selection.value.length > 1 && selectedSet.value.has(id)) select([id])
}

/** Writes the moves (rounded as stored), one patch per element. */
function commitMoves(ids: string[], label: string) {
  const positions = new Map<string, Point>()
  const next = new Map(moved.value)
  for (const id of ids) {
    const p = moved.value.get(id)
    if (!p) continue
    const rounded = { x: round2(p.x), y: round2(p.y) }
    positions.set(id, rounded)
    next.set(id, rounded)
  }
  moved.value = next
  editor.move(positions, label)
  settle(positions.keys())
}

// --- placing ----------------------------------------------------------------------------------------------------------

function itemsOnMap(): ReadonlySet<string> {
  return new Set(model.cardsOfItem.value.keys())
}

function placed(id: string | null) {
  tool.value = 'select'
  if (id) select([id])
}

function request<E>(
  at: Point,
  extra: E,
  add: (id: string, o?: { width?: number; height?: number }) => string | null,
): PlaceRequest<E> {
  return {
    at,
    extra,
    onMap: itemsOnMap(),
    place: (id: string, o?: { width?: number; height?: number }) => placed(add(id, o)),
    cancel: () => (tool.value = 'select'),
  }
}

function finishDraw() {
  const d = draft.value!
  draft.value = null
  const line = lineDraft.value
  lineDraft.value = null
  const k = pan.view.value.k
  const dragged = Math.abs(d.to.x - d.from.x) * k + Math.abs(d.to.y - d.from.y) * k > 6
  const at = snapOn.value ? snapPoint(d.from, gridSize.value) : d.from
  const centred = (s: { width: number; height: number }) => ({ x: at.x - s.width / 2, y: at.y - s.height / 2, ...s })
  const t = d.tool
  if (isItemTool(t)) {
    const s = SIZES.card
    emit(
      'requestItem',
      request(at, { type: t }, (itemId) =>
        editor.add({ kind: 'item', item_id: itemId, config: { size: 'compact' }, ...centred(s) }),
      ),
    )
  } else if (t === 'milestone') {
    emit(
      'requestMilestone',
      request(at, {}, (milestoneId) =>
        editor.add({ kind: 'milestone', milestone_id: milestoneId, ...centred(SIZES.milestone) }),
      ),
    )
  } else if (t === 'timeline') {
    emit(
      'requestProject',
      request(at, {}, (projectId) =>
        editor.add({
          kind: 'timeline',
          project_id: projectId,
          config: { orientation: 'horizontal', spacing: 'even', milestoneIds: null, showDates: true, showLabels: true },
          ...centred(SIZES.timeline),
        }),
      ),
    )
  } else if (t === 'image') {
    emit(
      'requestImage',
      request(at, { file: null }, (fileId, o) => addImage(fileId, at, o)),
    )
  } else if (t === 'text') {
    const id = editor.add({ kind: 'text', text: null, ...centred(SIZES.text) })
    placed(id)
    if (id) void nextTick(() => editWhenThere(id))
  } else if (t === 'rect' || t === 'ellipse') {
    const box = bandBox(d.from, d.to)
    const frame = dragged ? rectOf(box) : centred(SIZES[t])
    placed(editor.add({ kind: t, ...frame }))
  } else if (t === 'line' && line) {
    const end = dragged ? d.to : { x: d.from.x + SIZES.line.width, y: d.from.y }
    const target = dragged ? line.target : null
    placed(
      editor.add({
        kind: 'line',
        ...lineRow(d.from, end),
        start_element_id: line.startTarget,
        end_element_id: target && target !== line.startTarget ? target : null,
        style: { arrowEnd: 'arrow' },
      }),
    )
  }
}

function addImage(fileId: string, at: Point, o?: { width?: number; height?: number }) {
  let width: number = o?.width ?? SIZES.image.width
  let height: number = o?.height ?? SIZES.image.height
  const scale = Math.min(1, 480 / Math.max(width, height))
  width *= scale
  height *= scale
  return editor.add({ kind: 'image', file_id: fileId, x: at.x - width / 2, y: at.y - height / 2, width, height })
}

// --- inline text editing --------------------------------------------------------------------------------------------

const editingId = ref<string | null>(null)
const editText = ref('')
const textEditor = ref<HTMLTextAreaElement | null>(null)
const editFrame = computed<Rect | null>(() => {
  const id = editingId.value
  const el = id ? model.elementsById.value.get(id) : undefined
  if (!el) return null
  if (el.kind === 'line') {
    const g = lines.value.get(el.id)
    return g ? { x: g.mid.x - 100, y: g.mid.y - 22, width: 200, height: 44 } : null
  }
  return frames.value.get(el.id) ?? null
})
const editStyle = computed(() => {
  const el = editingId.value ? model.elementsById.value.get(editingId.value) : undefined
  const s = el?.style ?? {}
  return {
    fontSize: `${s.fontSize ?? (el?.kind === 'text' ? 16 : el?.kind === 'line' ? 12 : 14)}px`,
    fontWeight: s.bold ? 700 : 400,
    fontStyle: s.italic ? 'italic' : 'normal',
    textAlign: s.align ?? (el?.kind === 'text' ? 'left' : 'center'),
  }
})

/**
 * A text just placed is edited as soon as its row is in the model: the memory source has it at once, the device's
 * source after its write (a live query's round).
 */
let editPending: string | null = null
function editWhenThere(id: string) {
  editPending = null
  if (model.elementsById.value.has(id)) startEdit(id)
  else editPending = id
}
watch(
  () => model.elementsById.value,
  (byId) => {
    if (editPending && byId.has(editPending)) {
      const id = editPending
      editPending = null
      void nextTick(() => startEdit(id))
    }
  },
)

function startEdit(id: string) {
  const el = model.elementsById.value.get(id)
  if (!el || !isMovableOrLine(el) || !['rect', 'ellipse', 'text', 'line'].includes(el.kind)) return
  editingId.value = id
  editText.value = el.text ?? ''
  void nextTick(() => {
    textEditor.value?.focus()
    textEditor.value?.select()
  })
}
function isMovableOrLine(el: ElementRow) {
  return canEdit.value && !lockedLayers.value.has(el.layer_id) && !isMatrixCard(el.id)
}
function commitEdit() {
  const id = editingId.value
  editingId.value = null
  const el = id ? model.elementsById.value.get(id) : undefined
  if (el && (editText.value || null) !== (el.text ?? null)) editor.setText(el.id, editText.value)
  focusMap()
}
function onEditKey(e: KeyboardEvent) {
  e.stopPropagation()
  if (e.key === 'Escape') {
    editingId.value = null
    focusMap()
  } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    commitEdit()
  }
}
function onDoubleClick(e: MouseEvent) {
  const id = elementIdOf(e.target)
  if (id && tool.value === 'select') startEdit(id)
}

// --- keyboard and clipboard -----------------------------------------------------------------------------------------

/** The last copy, for paste where the system clipboard can't be read (and for duplicate). */
let clipboard: string | null = null

function copySelection(): string | null {
  const text = editor.copy(selection.value)
  if (text) clipboard = text
  return text
}

function pasteText(text: string | null) {
  if (!text || !canEdit.value) return
  const centre = centreOfView()
  const ids = editor.paste(text, model.mapId.value && text.includes(`"mapId":"${model.mapId.value}"`) ? null : centre)
  if (ids.length) select(ids)
  else if (!text.trimStart().startsWith('{')) {
    // Plain text becomes a text element.
    const id = editor.add({ kind: 'text', text, x: centre.x - 120, y: centre.y - 20, width: 240, height: 60 })
    if (id) select([id])
  }
}

function centreOfView(): Point {
  const v = visibleNow()
  return { x: (v.minX + v.maxX) / 2, y: (v.minY + v.maxY) / 2 }
}

function mapIsActive(): boolean {
  const active = document.activeElement
  return !!root.value && !!active && root.value.contains(active) && !isTyping(active)
}

function onCopy(e: ClipboardEvent) {
  if (!mapIsActive() || !selection.value.length) return
  const text = copySelection()
  if (text && e.clipboardData) {
    e.clipboardData.setData('text/plain', text)
    e.preventDefault()
  }
}
function onCut(e: ClipboardEvent) {
  if (!mapIsActive() || !selection.value.length || !canEdit.value) return
  onCopy(e)
  editor.remove(selection.value)
  select([])
}
function onPaste(e: ClipboardEvent) {
  if (!mapIsActive() || !canEdit.value) return
  const file = [...(e.clipboardData?.files ?? [])].find((f) => f.type.startsWith('image/'))
  e.preventDefault()
  if (file) {
    const at = centreOfView()
    emit(
      'requestImage',
      request(at, { file }, (fileId, o) => addImage(fileId, at, o)),
    )
    return
  }
  pasteText(e.clipboardData?.getData('text/plain') || clipboard)
}

function nudge(e: KeyboardEvent) {
  const ids = selection.value.filter(isMovable)
  if (!ids.length) return false
  const moved$ = moving.nudge(e, ids)
  if (!moved$.length) return false
  commitMoves(moved$, 'Nudge')
  return true
}

function onKeydown(e: KeyboardEvent) {
  if (isTyping(e.target)) return
  if (e.key === ' ' || e.code === 'Space') {
    spaceHeld.value = true
    e.preventDefault()
    return
  }
  if (e.key.startsWith('Arrow') && !e.ctrlKey && !e.metaKey) {
    if (nudge(e)) return
  }
  const command = commandFor(e)
  if (!command) return
  switch (command) {
    case 'delete':
      if (!canEdit.value) return
      editor.remove(selection.value)
      select([])
      break
    case 'undo':
    case 'redo': {
      if (command === 'undo') undo()
      else redo()
      break
    }
    case 'copy':
      // The copy event writes the system clipboard; this keeps a copy for paste and duplicate.
      copySelection()
      return
    case 'cut':
    case 'paste':
      // The cut and paste events follow.
      return
    case 'duplicate': {
      const ids = editor.duplicate(selection.value)
      if (ids.length) select(ids)
      break
    }
    case 'forward':
    case 'backward':
    case 'toFront':
    case 'toBack':
      editor.reorder(command, selection.value)
      break
    case 'selectAll':
      select(layerLists.value.filter((l) => !l.layer.is_locked).flatMap((l) => l.elements.map((el) => el.id)))
      break
    case 'fit':
      fit()
      break
    case 'zoomIn':
      zoomBy(1.25)
      break
    case 'zoomOut':
      zoomBy(0.8)
      break
    case 'escape':
      if (gesture || band.value || draft.value || lineDraft.value) cancelGesture()
      else if (tool.value !== 'select') tool.value = 'select'
      else closePanel()
      break
  }
  e.preventDefault()
}

/** Undo and redo select what they touched, as far as it is still there. */
function undo() {
  const touched = editor.undo()
  if (touched) void nextTick(() => select(touched.filter((id) => model.elementsById.value.has(id))))
}
function redo() {
  const touched = editor.redo()
  if (touched) void nextTick(() => select(touched.filter((id) => model.elementsById.value.has(id))))
}

function openProperties() {
  selection.value = []
  panelItem.value = null
  panelMap.value = true
}

function onKeyup(e: KeyboardEvent) {
  if (e.key === ' ' || e.code === 'Space') spaceHeld.value = false
}

function cancelGesture() {
  if (gesture?.kind === 'resize') resized.value = new Map()
  gesture = null
  band.value = null
  draft.value = null
  lineDraft.value = null
}

// --- view -------------------------------------------------------------------------------------------------------------

const transform = computed(() => `translate(${pan.view.value.x} ${pan.view.value.y}) scale(${pan.view.value.k})`)
const k = computed(() => pan.view.value.k)

function contentBox(ids?: readonly string[]): Box {
  const set = ids ? new Set(ids) : null
  const boxes: Box[] = []
  for (const el of candidates.value) {
    if ((set ? set.has(el.id) : visible.value.has(el.id)) && model.elementsById.value.has(el.id)) {
      boxes.push(boxOfElement(el))
    }
  }
  if (!set && props.world) boxes.push(props.world)
  return boundsOf(
    boxes.map((b) => rectOf(b)),
    props.world ?? { minX: 0, minY: 0, maxX: size.value.width, maxY: size.value.height },
  )
}
function fitBox(box: Box) {
  measure()
  pan.view.value = fitTo(padBox(box, 16), size.value.width, size.value.height)
}
function fit() {
  fitBox(contentBox())
}
function fitSelection() {
  if (selection.value.length) fitBox(contentBox(selection.value))
}
function zoomBy(factor: number) {
  pan.view.value = zoomAround(pan.view.value, { x: size.value.width / 2, y: size.value.height / 2 }, factor)
}
function zoomReset() {
  zoomBy(1 / pan.view.value.k)
}
function setView(view: View) {
  pan.view.value = { x: view.x, y: view.y, k: Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, view.k)) }
}
function centreAt(p: Point) {
  const v = pan.view.value
  pan.view.value = { k: v.k, x: size.value.width / 2 - p.x * v.k, y: size.value.height / 2 - p.y * v.k }
}

let viewTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => pan.view.value,
  (v) => {
    clearTimeout(viewTimer)
    viewTimer = setTimeout(() => {
      viewport = { x: Math.round(v.x), y: Math.round(v.y), k: Math.round(v.k * 1000) / 1000 }
      saveSettings()
    }, 1500)
  },
)

const minimapBoxes = computed(() => candidates.value.filter((el) => visible.value.has(el.id)).map(boxOfElement))
const minimapBounds = computed(() =>
  minimapBoxes.value.length
    ? boundsOf(minimapBoxes.value.map(rectOf), { minX: 0, minY: 0, maxX: 1, maxY: 1 })
    : (props.world ?? { minX: 0, minY: 0, maxX: size.value.width, maxY: size.value.height }),
)
const viewportBox = computed(() => visibleNow())

// Deep link: once per focus value, when its elements are there.
let focused: string | null = null
function applyFocus(): boolean {
  const f = parseFocus(props.focus, ['item', 'element'] as const)
  if (!f || focused === props.focus) return false
  const ids =
    f.kind === 'item'
      ? [...(model.cardsOfItem.value.get(f.id) ?? [])]
      : model.elementsById.value.has(f.id)
        ? [f.id]
        : []
  if (!ids.length) return false
  focused = props.focus
  select(ids)
  void nextTick(() => fitBox(contentBox(ids)))
  return true
}

let initialised = false
function initialise() {
  if (initialised || !model.map.value) return
  initialised = true
  measure()
  if (applyFocus()) return
  const saved = props.source.viewSettings.value?.viewport
  if (saved && Number.isFinite(saved.k)) setView(saved)
  else void nextTick(fit)
}
watch([() => props.focus, () => model.drawingOrder.value.length], () => (initialised ? applyFocus() : initialise()))

onMounted(() => {
  measure()
  if (typeof ResizeObserver !== 'undefined' && svg.value) {
    resizeObserver = new ResizeObserver(measure)
    resizeObserver.observe(svg.value)
  }
  document.addEventListener('copy', onCopy)
  document.addEventListener('cut', onCut)
  document.addEventListener('paste', onPaste)
  window.addEventListener('blur', onBlur)
  void nextTick(initialise)
})
function onBlur() {
  spaceHeld.value = false
}
onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  document.removeEventListener('copy', onCopy)
  document.removeEventListener('cut', onCut)
  document.removeEventListener('paste', onPaste)
  window.removeEventListener('blur', onBlur)
  clearTimeout(saveTimer)
  clearTimeout(viewTimer)
  clearTimeout(settleTimer)
  clearTimeout(settleViewTimer)
})

// --- the filter bar's options ---------------------------------------------------------------------------------------

const itemsShown = computed(() => {
  const out = new Map<string, NonNullable<ReturnType<typeof model.itemsById.value.get>>>()
  for (const el of candidates.value) {
    const item = el.item_id ? model.itemsById.value.get(el.item_id) : undefined
    if (item) out.set(item.id, item)
  }
  return [...out.values()]
})
const statusOptions = computed<Option[]>(() => {
  const ids = new Set(itemsShown.value.map((i) => i.status_id).filter((s): s is string => !!s))
  return [...ids]
    .map((id) => model.statusesById.value.get(id))
    .filter((s) => !!s)
    .sort((a, b) => a!.item_type.localeCompare(b!.item_type) || a!.sort_order - b!.sort_order)
    .map((s) => ({ value: s!.id, label: `${s!.name} (${s!.item_type})` }))
})
const personOptions = computed<Option[]>(() => {
  const ids = new Set<string>()
  for (const item of itemsShown.value) for (const p of model.personsByItem.value.get(item.id) ?? []) ids.add(p)
  return [...ids]
    .map((id) => ({ value: id, label: model.personsById.value.get(id)?.display_name ?? 'Unknown person' }))
    .sort((a, b) => a.label.localeCompare(b.label))
})
const colorOptions = computed<Option[]>(() => {
  const keys = new Set(itemsShown.value.map(colorKey))
  return [...keys].map((key) => ({ value: key, label: key === 'default' ? 'Type colour' : key }))
})
const scoreOptions = computed<Option[]>(() =>
  Object.values(model.defsByKey.value)
    .filter((d) => !d.isArchived && ['score', 'rating', 'number'].includes(d.type))
    .map((d) => ({ value: d.key, label: d.label })),
)
const quadrantOptions = computed<Option[] | null>(() => {
  if (!props.quadrants) return null
  const labels = model.map.value?.matrix?.quadrants ?? {}
  return (['tr', 'tl', 'br', 'bl'] as const).map((q) => ({ value: q, label: labels[q] || DEFAULT_QUADRANT_LABELS[q] }))
})
const canManage = computed(() => canEdit.value && props.source.access.value === 'manage')
const layerCounts = computed(
  () => new Map([...model.elementsByLayer.value].map(([id, list]) => [id, list.length] as [string, number])),
)

const background = computed(() => cssColor(model.settings.value.background) ?? 'var(--pgpm-panel)')
const gridPattern = computed(() => {
  const s = gridSize.value * pan.view.value.k
  return s >= 6 ? { size: gridSize.value, transform: transform.value } : null
})
const patternId = `map-grid-${Math.random().toString(36).slice(2, 8)}`
const handleSize = computed(() => 8 / pan.view.value.k)

function hideLayerLocally(id: string, hidden: boolean) {
  const set = new Set(hiddenLocally.value)
  if (hidden) set.add(id)
  else set.delete(id)
  updateSettings({ hiddenLayerIds: [...set] })
}

/** A card of the item at a map point (default: the middle of the view) – the host's library panel, *Add to map…*. */
function addItemCard(itemId: string, at?: Point | null): string | null {
  const p = at ?? centreOfView()
  const s = SIZES.card
  const id = editor.add({
    kind: 'item',
    item_id: itemId,
    config: { size: 'compact' },
    x: p.x - s.width / 2,
    y: p.y - s.height / 2,
    ...s,
  })
  if (id) select([id])
  return id
}

function openTrayItem(itemId: string) {
  selection.value = []
  panelMap.value = false
  panelItem.value = itemId
}

defineExpose({
  editor,
  model,
  pan,
  selection,
  select,
  fit,
  setView,
  view: pan.view,
  drawnCount,
  openItemPanel: openTrayItem,
  addItemCard,
  /** A screen point (e.g. a drop) on the map. */
  toWorld: (clientX: number, clientY: number) => pan.toWorld({ clientX, clientY }),
  copy: copySelection,
  paste: pasteText,
})
</script>

<template>
  <div
    ref="root"
    class="map-canvas"
    :class="[`tool-${tool}`, { panning: spaceHeld, readonly: !canEdit }]"
    tabindex="0"
    data-testid="map-canvas"
    @keydown="onKeydown"
    @keyup="onKeyup"
  >
    <div class="stage">
      <svg
        ref="svg"
        class="surface"
        role="application"
        aria-label="Map"
        :style="{ background }"
        @wheel="pan.onWheel"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @dblclick="onDoubleClick"
      >
        <defs v-if="grid && gridPattern">
          <pattern
            :id="patternId"
            patternUnits="userSpaceOnUse"
            :width="gridPattern.size"
            :height="gridPattern.size"
            :patternTransform="gridPattern.transform"
          >
            <circle :cx="0" :cy="0" :r="1.2 / k" class="grid-dot" />
          </pattern>
        </defs>
        <rect v-if="grid && gridPattern" class="grid" width="100%" height="100%" :fill="`url(#${patternId})`" />
        <g :transform="transform">
          <slot name="background" />
          <SceneLayer
            v-for="list in layerLists"
            :key="list.layer.id"
            :layer="list.layer"
            :elements="list.elements"
            :frames="frames"
            :lines="lines"
            :scene="lookups"
            :detail="detail"
            :selected="selectedSet"
            :editing-id="editingId"
            :animated="animated && list.layer.id === virtual?.layer.id"
          />
          <g v-if="linkPaths.length" class="links" aria-hidden="true">
            <path v-for="l in linkPaths" :key="l.id" :d="l.d" :stroke-width="1.5 / Math.max(0.5, k)" />
          </g>
          <!-- the selection: outlines, resize handles, line ends -->
          <g class="selection" aria-hidden="true">
            <rect
              v-for="s in selectedFrames"
              :key="s.id"
              class="outline"
              :x="s.frame.x"
              :y="s.frame.y"
              :width="s.frame.width"
              :height="s.frame.height"
              :stroke-width="1.5 / k"
            />
            <path v-for="s in selectedLines" :key="s.id" class="outline-line" :d="s.geometry.d" :stroke-width="6 / k" />
            <template v-if="resizable">
              <rect
                v-for="h in HANDLES"
                :key="h"
                class="handle"
                :data-handle="h"
                :x="handlePoint(resizable.frame, h).x - handleSize / 2"
                :y="handlePoint(resizable.frame, h).y - handleSize / 2"
                :width="handleSize"
                :height="handleSize"
                :stroke-width="1.5 / k"
                :style="{ cursor: HANDLE_CURSORS[h] }"
              />
            </template>
            <template v-if="lineEditable && !lineDraft">
              <circle
                class="handle end"
                data-line-end="start"
                :cx="lineEditable.geometry.start.x"
                :cy="lineEditable.geometry.start.y"
                :r="handleSize * 0.75"
                :stroke-width="1.5 / k"
              />
              <circle
                class="handle end"
                data-line-end="end"
                :cx="lineEditable.geometry.end.x"
                :cy="lineEditable.geometry.end.y"
                :r="handleSize * 0.75"
                :stroke-width="1.5 / k"
              />
            </template>
            <rect
              v-if="lineDraft?.target && frames.get(lineDraft.target)"
              class="target"
              :x="frames.get(lineDraft.target)!.x - 4 / k"
              :y="frames.get(lineDraft.target)!.y - 4 / k"
              :width="frames.get(lineDraft.target)!.width + 8 / k"
              :height="frames.get(lineDraft.target)!.height + 8 / k"
              :stroke-width="2 / k"
            />
          </g>
          <rect v-if="band" class="band" v-bind="rectOf(bandBox(band.from, band.to))" :stroke-width="1 / k" />
          <template v-if="draft">
            <line
              v-if="draft.tool === 'line'"
              class="draft"
              :x1="draft.from.x"
              :y1="draft.from.y"
              :x2="draft.to.x"
              :y2="draft.to.y"
              :stroke-width="2 / k"
            />
            <ellipse
              v-else-if="draft.tool === 'ellipse'"
              class="draft"
              :cx="(draft.from.x + draft.to.x) / 2"
              :cy="(draft.from.y + draft.to.y) / 2"
              :rx="Math.abs(draft.to.x - draft.from.x) / 2"
              :ry="Math.abs(draft.to.y - draft.from.y) / 2"
              :stroke-width="1.5 / k"
            />
            <rect
              v-else-if="draft.tool === 'rect'"
              class="draft"
              v-bind="rectOf(bandBox(draft.from, draft.to))"
              :stroke-width="1.5 / k"
            />
          </template>
          <foreignObject
            v-if="editingId && editFrame"
            :x="editFrame.x"
            :y="editFrame.y"
            :width="editFrame.width"
            :height="editFrame.height"
          >
            <textarea
              ref="textEditor"
              v-model="editText"
              xmlns="http://www.w3.org/1999/xhtml"
              class="inline-editor"
              :style="editStyle"
              aria-label="Text"
              @keydown="onEditKey"
              @blur="commitEdit"
              @pointerdown.stop
            />
          </foreignObject>
        </g>
      </svg>

      <div class="top">
        <MapFilterBar
          :filter="filter"
          :statuses="statusOptions"
          :persons="personOptions"
          :colors="colorOptions"
          :score-fields="scoreOptions"
          :quadrants="quadrantOptions"
          :show-links="showLinks"
          :can-save-default="canManage"
          @update="(f) => updateSettings(f)"
          @show-links="(on) => updateSettings({ showLinks: on })"
          @save-default="editor.patchMap({ default_view: filter }, 'Save default view')"
        />
      </div>
      <div class="left">
        <MapToolbar
          v-model:tool="tool"
          :tools="tools"
          :can-edit="canEdit"
          :can-undo="editor.history.canUndo.value"
          :can-redo="editor.history.canRedo.value"
          :undo-label="editor.history.undoLabel.value"
          :redo-label="editor.history.redoLabel.value"
          :layers-open="layersOpen"
          @undo="undo"
          @redo="redo"
          @layers="layersOpen = !layersOpen"
          @properties="openProperties"
        />
        <LayerPanel
          v-if="layersOpen"
          :layers="model.layers.value"
          :active-id="editor.activeLayer.value?.id ?? null"
          :can-edit="canEdit"
          :hidden-locally="hiddenLocally"
          :counts="layerCounts"
          @add="editor.addLayer(`Layer ${model.layers.value.length + 1}`)"
          @activate="(id) => (editor.chosenLayerId.value = id)"
          @rename="(id, name) => editor.renameLayer(id, name)"
          @visible="(id, on) => editor.setLayerVisible(id, on)"
          @hide-locally="hideLayerLocally"
          @locked="(id, on) => editor.setLayerLocked(id, on)"
          @move="(id, d) => editor.moveLayer(id, d)"
          @delete="(id, mode) => editor.deleteLayer(id, mode)"
          @close="layersOpen = false"
        />
      </div>
      <slot name="aside" :open-item="openTrayItem" />
      <div class="bottom-left">
        <MiniMap
          v-if="showMinimap"
          :boxes="minimapBoxes"
          :bounds="minimapBounds"
          :viewport="viewportBox"
          @centre="centreAt"
        />
        <slot name="overlay" :drawn="drawnCount" />
      </div>
      <div class="bottom-right">
        <ZoomControls
          :zoom="k"
          :grid="grid"
          :snap="snapOn"
          :can-snap="canEdit"
          :minimap="showMinimap"
          :has-selection="selection.length > 0"
          @zoom-in="zoomBy(1.25)"
          @zoom-out="zoomBy(0.8)"
          @reset="zoomReset"
          @fit="fit"
          @fit-selection="fitSelection"
          @grid="toggleGrid"
          @snap="toggleSnap"
          @minimap="updateSettings({ showMinimap: !showMinimap })"
        />
      </div>
    </div>
    <MapSidePanel
      :open="panelOpen"
      :content="shown"
      :model="model"
      :editor="editor"
      :quadrants="quadrantLabels"
      :now="now"
      :milestone-editor="!!slots['milestone-panel']"
      @close="closePanel"
      @select="select"
      @open-item="(id) => emit('openItem', id)"
    >
      <template #item-panel="slot"><slot name="item-panel" v-bind="slot" /></template>
      <template #milestone-panel="slot"><slot name="milestone-panel" v-bind="slot" /></template>
      <template #map-sharing="slot"><slot name="map-sharing" v-bind="slot" /></template>
    </MapSidePanel>
  </div>
</template>

<style scoped>
.map-canvas {
  position: relative;
  height: 100%;
  min-height: 420px;
  outline: none;
}

.stage {
  position: absolute;
  inset: 0;
  overflow: hidden;
  border: 1px solid var(--pgpm-border);
  border-radius: 6px;
}

.surface {
  width: 100%;
  height: 100%;
  display: block;
  touch-action: none;
  user-select: none;
  cursor: default;
}

.tool-hand .surface,
.panning .surface {
  cursor: grab;
}

.tool-rect .surface,
.tool-ellipse .surface,
.tool-line .surface,
.tool-text .surface,
.tool-note .surface,
.tool-idea .surface,
.tool-lead .surface,
.tool-project .surface,
.tool-milestone .surface,
.tool-timeline .surface,
.tool-image .surface {
  cursor: crosshair;
}

.grid {
  pointer-events: none;
}

.grid-dot {
  fill: color-mix(in srgb, var(--pgpm-muted) 45%, transparent);
}

.links path {
  fill: none;
  stroke: var(--p-primary-color);
  stroke-dasharray: 6 5;
  opacity: 0.7;
  pointer-events: none;
}

.selection .outline,
.selection .target {
  fill: none;
  stroke: var(--p-primary-color);
  pointer-events: none;
}

.selection .target {
  stroke-dasharray: 4 3;
}

.selection .outline-line {
  fill: none;
  stroke: color-mix(in srgb, var(--p-primary-color) 35%, transparent);
  pointer-events: none;
}

.selection .handle {
  fill: var(--pgpm-panel);
  stroke: var(--p-primary-color);
}

.selection .handle.end {
  cursor: move;
}

.band {
  fill: color-mix(in srgb, var(--p-primary-color) 8%, transparent);
  stroke: var(--p-primary-color);
  stroke-dasharray: 4 3;
  pointer-events: none;
}

.draft {
  fill: color-mix(in srgb, var(--p-primary-color) 6%, transparent);
  stroke: var(--p-primary-color);
  stroke-dasharray: 5 4;
  pointer-events: none;
}

.inline-editor {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  resize: none;
  border: 1px solid var(--p-primary-color);
  border-radius: 4px;
  padding: 4px 8px;
  background: color-mix(in srgb, var(--pgpm-panel) 92%, transparent);
  color: var(--pgpm-text);
  font-family: var(--pgpm-font);
  line-height: 1.3;
  outline: none;
}

.top {
  position: absolute;
  top: var(--pgpm-space-2);
  left: calc(var(--pgpm-space-2) + 48px);
  right: var(--pgpm-space-2);
  display: flex;
  pointer-events: none;
}

.top > * {
  pointer-events: auto;
}

.left {
  position: absolute;
  top: var(--pgpm-space-2);
  left: var(--pgpm-space-2);
  bottom: 140px;
  display: flex;
  align-items: flex-start;
  gap: var(--pgpm-space-2);
}

.left :deep(.layer-panel) {
  margin-top: 52px;
}

.bottom-left {
  position: absolute;
  left: var(--pgpm-space-2);
  bottom: var(--pgpm-space-2);
  display: flex;
  align-items: flex-end;
  gap: var(--pgpm-space-2);
}

.bottom-right {
  position: absolute;
  right: var(--pgpm-space-2);
  bottom: var(--pgpm-space-2);
}

@media (max-width: 599px) {
  .bottom-left {
    display: none;
  }
}
</style>
