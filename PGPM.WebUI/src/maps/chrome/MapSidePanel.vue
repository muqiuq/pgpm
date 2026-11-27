<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Drawer from 'primevue/drawer'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, nextTick, ref, watch } from 'vue'
import { milestoneTone, TONE_COLORS, TYPE_LABELS } from '../cards'
import type { MapEditor, ZOrder } from '../editor'
import { isTyping } from '../keyboard'
import { isMatrixCard } from '../matrixItems'
import type { MapModel } from '../model'
import type { PanelContent } from '../sceneTypes'
import type { ElementKind, ElementRow } from '../types'
import ItemSummary from './ItemSummary.vue'
import MapProperties from './MapProperties.vue'
import MilestoneSummary from './MilestoneSummary.vue'
import StyleEditor from './StyleEditor.vue'

/**
 * The side panel (doc 01 §12.5, docs/03 §13.3): PrimeVue's Drawer from the right, non-modal and not dismissed by a
 * click outside – the map stays usable, another element swaps the content. PNMTD DevicePanel's layout: a header with a
 * coloured avatar, title and status, a row of actions, then stacked sections. `content` is the canvas' `shown`, the
 * last selection, kept while the drawer slides out. Focus: opening it leaves the focus on the map (its shortcuts keep
 * working); F6 (or Enter) on the map comes here (`focusPanel`, the title), and closing it with the focus inside –
 * Escape, the close button – hands it back to the map (`leave`).
 */
const props = defineProps<{
  open: boolean
  content: PanelContent | null
  model: MapModel
  editor: MapEditor
  quadrants: ReadonlyMap<string, string> | null
  now: number
  /** The host's `#milestone-panel` edits the Definition of Done: the summary leaves it out. */
  milestoneEditor?: boolean
}>()
const emit = defineEmits<{ close: []; leave: []; select: [ids: string[]]; openItem: [itemId: string] }>()

const panel = ref<HTMLElement | null>(null)
const heading = ref<HTMLElement | null>(null)
/** The keyboard's way in: the panel's title (read out with the panel's name), Tab goes on from there. */
function focusPanel() {
  void nextTick(() => heading.value?.focus())
}
// Closing with the focus inside: back to the map, not to the page's top.
watch(
  () => props.open,
  (open) => {
    const active = document.activeElement
    if (!open && panel.value && active && (panel.value.contains(active) || active === document.body)) emit('leave')
  },
)
/** Escape in the panel closes it – not while a field takes it (an inline editor discards its draft first). */
function onEscape(e: KeyboardEvent) {
  if (e.defaultPrevented || isTyping(e.target)) return
  e.stopPropagation()
  emit('close')
}
defineExpose({ focusPanel })

const KIND_LABELS: Record<ElementKind, string> = {
  item: 'Card',
  milestone: 'Milestone',
  timeline: 'Timeline',
  rect: 'Rectangle',
  ellipse: 'Ellipse',
  line: 'Line',
  text: 'Text',
  image: 'Image',
}
const KIND_ICONS: Record<ElementKind, string> = {
  item: 'pi pi-id-card',
  milestone: 'pi pi-bullseye',
  timeline: 'pi pi-sliders-h',
  rect: 'pi pi-stop',
  ellipse: 'pi pi-circle',
  line: 'pi pi-arrow-up-right',
  text: 'pi pi-align-left',
  image: 'pi pi-image',
}
const DRAWINGS: readonly ElementKind[] = ['rect', 'ellipse', 'line', 'text', 'image']

const elements = computed<ElementRow[]>(() =>
  props.content?.kind === 'elements'
    ? props.content.ids.map((id) => props.model.elementsById.value.get(id)).filter((e): e is ElementRow => !!e)
    : [],
)
const single = computed(() => (elements.value.length === 1 ? elements.value[0]! : null))
const itemId = computed(() =>
  props.content?.kind === 'item' ? props.content.itemId : single.value?.kind === 'item' ? single.value.item_id : null,
)
const item = computed(() => (itemId.value ? props.model.itemsById.value.get(itemId.value) : undefined))
const card = computed(() => (itemId.value ? props.model.cards.value.get(itemId.value) : undefined))
const milestone = computed(() =>
  single.value?.kind === 'milestone' && single.value.milestone_id
    ? props.model.milestonesById.value.get(single.value.milestone_id)
    : undefined,
)
const timelineProject = computed(() =>
  single.value?.kind === 'timeline' && single.value.project_id
    ? props.model.itemsById.value.get(single.value.project_id)
    : undefined,
)
const stored = computed(() => elements.value.filter((e) => !isMatrixCard(e.id)))
const drawings = computed(() => stored.value.filter((e) => DRAWINGS.includes(e.kind)))
const lockedLayer = computed(() =>
  stored.value.some((e) => props.model.layerById.value.get(e.layer_id)?.is_locked ?? false),
)
const canEdit = computed(() => props.editor.canEdit.value)
const canChange = computed(() => canEdit.value && !lockedLayer.value && stored.value.length > 0)
const map = computed(() => props.model.map.value)

const header = computed(() => {
  const c = props.content
  if (c?.kind === 'map') {
    return {
      icon: 'pi pi-map',
      color: 'var(--p-primary-color)',
      title: map.value?.name ?? 'Map',
      sub: map.value?.kind === 'matrix' ? 'Matrix map' : 'Canvas map',
    }
  }
  if (itemId.value) {
    if (!item.value || !card.value)
      return { icon: 'pi pi-lock', color: 'var(--pgpm-none)', title: 'Restricted', sub: 'You can’t see this item' }
    return {
      icon: card.value.icon,
      color: card.value.color,
      title: `${card.value.key} ${card.value.title}`,
      sub: TYPE_LABELS[item.value.type],
    }
  }
  if (milestone.value) {
    return {
      icon: milestone.value.kind === 'gate' ? 'pi pi-stop' : 'pi pi-circle-fill',
      color: TONE_COLORS[milestoneTone(milestone.value)],
      title: milestone.value.title,
      sub: milestone.value.kind === 'gate' ? 'Gate' : 'Milestone',
    }
  }
  if (single.value) {
    const layer = props.model.layerById.value.get(single.value.layer_id)
    return {
      icon: KIND_ICONS[single.value.kind],
      color: 'var(--pgpm-none)',
      title:
        single.value.kind === 'timeline'
          ? `Timeline${timelineProject.value ? ` · ${timelineProject.value.title}` : ''}`
          : KIND_LABELS[single.value.kind],
      sub: layer ? `Layer ${layer.name}${layer.is_locked ? ' (locked)' : ''}` : '',
    }
  }
  if (elements.value.length > 1) {
    return {
      icon: 'pi pi-clone',
      color: 'var(--pgpm-none)',
      title: `${elements.value.length} elements`,
      sub: 'Selection',
    }
  }
  return { icon: 'pi pi-question', color: 'var(--pgpm-none)', title: 'Nothing selected', sub: '' }
})

const layerOptions = computed(() =>
  [...props.model.layers.value]
    .reverse()
    .filter((l) => !l.is_locked)
    .map((l) => ({ value: l.id, label: l.name })),
)
const currentLayer = computed(() => {
  const ids = new Set(stored.value.map((e) => e.layer_id))
  return ids.size === 1 ? [...ids][0]! : null
})
const ids = () => stored.value.map((e) => e.id)
const reorder = (op: ZOrder) => props.editor.reorder(op, ids())
function duplicate() {
  const created = props.editor.duplicate(ids())
  if (created.length) emit('select', created)
}
function remove() {
  props.editor.remove(ids())
  emit('select', [])
}

// The text of a single shape, line or text, saved when the field is left.
const text = ref('')
watch(
  single,
  (el) => {
    text.value = el?.text ?? ''
  },
  { immediate: true },
)
function commitText() {
  const el = single.value
  if (el && (text.value || null) !== (el.text ?? null)) props.editor.setText(el.id, text.value)
}

const cardSize = computed(() => (single.value?.config?.size === 'expanded' ? 'expanded' : 'compact'))
function toggleCardSize() {
  const el = single.value
  if (el) props.editor.setConfig([el.id], { size: cardSize.value === 'expanded' ? 'compact' : 'expanded' })
}

const projectMilestones = computed(() =>
  item.value?.type === 'project' ? (props.model.milestonesByProject.value.get(item.value.id) ?? []) : [],
)
const quadrant = computed(() => (itemId.value ? (props.quadrants?.get(itemId.value) ?? null) : null))
const badgeFields = computed(() =>
  Object.values(props.model.defsByKey.value)
    .filter((d) => !d.isArchived && ['score', 'rating', 'number', 'select'].includes(d.type))
    .map((d) => ({ value: d.key, label: d.label })),
)
const orientations = [
  { label: 'Horizontal', value: 'horizontal' },
  { label: 'Vertical', value: 'vertical' },
]
const spacings = [
  { label: 'Even', value: 'even' },
  { label: 'By date', value: 'date' },
]
</script>

<template>
  <Drawer
    :visible="open"
    position="right"
    :modal="false"
    :dismissable="false"
    :close-on-escape="false"
    class="map-drawer"
    aria-label="Details"
    @update:visible="(v: boolean) => !v && emit('close')"
  >
    <template #container="{ closeCallback }">
      <div ref="panel" class="map-panel" @keydown.esc="onEscape">
        <header class="head">
          <span class="avatar" :style="{ '--avatar': header.color }" aria-hidden="true"
            ><i :class="header.icon"
          /></span>
          <div class="title">
            <h2 ref="heading" tabindex="-1">{{ header.title }}</h2>
            <div class="sub">
              <span v-if="card?.status && itemId" class="status" :style="{ '--status': card.status.color }">{{
                card.status.name
              }}</span>
              <span v-if="card?.overdue && itemId" class="overdue">overdue</span>
              <span class="muted">{{ header.sub }}</span>
            </div>
          </div>
          <Button icon="pi pi-times" text rounded severity="secondary" aria-label="Close" @click="closeCallback" />
        </header>
        <div v-if="itemId || canChange" class="actions">
          <Button
            v-if="item"
            label="Open details"
            icon="pi pi-external-link"
            size="small"
            severity="secondary"
            @click="emit('openItem', item.id)"
          />
          <Button
            v-if="canChange && single?.kind === 'item'"
            :label="cardSize === 'expanded' ? 'Compact card' : 'Expand card'"
            size="small"
            severity="secondary"
            text
            @click="toggleCardSize"
          />
          <template v-if="canChange">
            <span class="group" role="group" aria-label="Order">
              <Button
                v-tooltip.bottom="'Bring to front'"
                icon="pi pi-angle-double-up"
                size="small"
                text
                severity="secondary"
                aria-label="Bring to front"
                @click="reorder('toFront')"
              />
              <Button
                v-tooltip.bottom="'Bring forward'"
                icon="pi pi-angle-up"
                size="small"
                text
                severity="secondary"
                aria-label="Bring forward"
                @click="reorder('forward')"
              />
              <Button
                v-tooltip.bottom="'Send backward'"
                icon="pi pi-angle-down"
                size="small"
                text
                severity="secondary"
                aria-label="Send backward"
                @click="reorder('backward')"
              />
              <Button
                v-tooltip.bottom="'Send to back'"
                icon="pi pi-angle-double-down"
                size="small"
                text
                severity="secondary"
                aria-label="Send to back"
                @click="reorder('toBack')"
              />
            </span>
            <Button
              v-tooltip.bottom="'Duplicate'"
              icon="pi pi-copy"
              size="small"
              text
              severity="secondary"
              aria-label="Duplicate"
              @click="duplicate"
            />
            <Button
              v-tooltip.bottom="'Remove from the map'"
              icon="pi pi-trash"
              size="small"
              text
              severity="danger"
              aria-label="Remove from the map"
              @click="remove"
            />
          </template>
        </div>
        <div class="body">
          <!-- an item card, or an item of the matrix tray -->
          <template v-if="itemId">
            <slot v-if="item" name="item-panel" :item="item" :element="single" :can-edit="canEdit" />
            <ItemSummary
              v-if="item && card"
              :item="item"
              :card="card"
              :card-count="model.cardsOfItem.value.get(item.id)?.length ?? 0"
              :milestones="projectMilestones"
              :links="model.links.value"
              :items-by-id="model.itemsById.value"
              :quadrant="quadrant"
              @select-all="emit('select', model.cardsOfItem.value.get(item.id) ?? [])"
            />
            <p v-else class="muted">This item isn’t shared with you. The card shows where it is.</p>
          </template>
          <template v-else-if="milestone">
            <slot name="milestone-panel" :milestone="milestone" :can-edit="canEdit" />
            <MilestoneSummary
              :milestone="milestone"
              :project="model.itemsById.value.get(milestone.item_id) ?? null"
              :dod="model.dodByMilestone.value.get(milestone.id) ?? []"
              :now="now"
              :show-dod="!milestoneEditor"
            />
          </template>
          <template v-else-if="content?.kind === 'map' && map">
            <MapProperties
              :map="map"
              :can-edit="canEdit"
              :badge-fields="badgeFields"
              @patch="(values) => editor.patchMap(values)"
            >
              <template #sharing><slot name="map-sharing" :map="map" /></template>
            </MapProperties>
          </template>
          <section v-if="single?.kind === 'timeline'" class="form">
            <h3>Timeline</h3>
            <SelectButton
              :model-value="single.config?.orientation ?? 'horizontal'"
              :options="orientations"
              option-label="label"
              option-value="value"
              size="small"
              :allow-empty="false"
              :disabled="!canChange"
              aria-label="Orientation"
              @update:model-value="(v: string) => editor.setConfig([single!.id], { orientation: v as 'horizontal' })"
            />
            <SelectButton
              :model-value="single.config?.spacing ?? 'even'"
              :options="spacings"
              option-label="label"
              option-value="value"
              size="small"
              :allow-empty="false"
              :disabled="!canChange"
              aria-label="Spacing"
              @update:model-value="(v: string) => editor.setConfig([single!.id], { spacing: v as 'even' })"
            />
            <label class="check">
              <Checkbox
                :model-value="single.config?.showLabels !== false"
                binary
                input-id="timeline-labels"
                :disabled="!canChange"
                @update:model-value="(v: boolean) => editor.setConfig([single!.id], { showLabels: v })"
              />
              <span>Titles</span>
            </label>
            <label class="check">
              <Checkbox
                :model-value="single.config?.showDates !== false"
                binary
                input-id="timeline-dates"
                :disabled="!canChange"
                @update:model-value="(v: boolean) => editor.setConfig([single!.id], { showDates: v })"
              />
              <span>Dates</span>
            </label>
          </section>
          <section v-if="single && ['rect', 'ellipse', 'line', 'text'].includes(single.kind)" class="form">
            <h3>Text</h3>
            <Textarea v-model="text" rows="2" auto-resize :disabled="!canChange" aria-label="Text" @blur="commitText" />
          </section>
          <section v-if="drawings.length && drawings.length === stored.length">
            <h3>Style</h3>
            <StyleEditor
              :elements="drawings"
              :disabled="!canChange"
              @change="
                (style) =>
                  editor.setStyle(
                    drawings.map((e) => e.id),
                    style,
                  )
              "
            />
          </section>
          <section v-if="elements.length > 1 && canChange" class="form">
            <h3>Layer</h3>
            <Select
              :model-value="currentLayer"
              :options="layerOptions"
              option-label="label"
              option-value="value"
              size="small"
              placeholder="Move to layer…"
              aria-label="Move to layer"
              @update:model-value="(id: string) => editor.moveToLayer(ids(), id)"
            />
          </section>
          <section v-else-if="single && canChange && layerOptions.length > 1" class="form">
            <h3>Layer</h3>
            <Select
              :model-value="single.layer_id"
              :options="layerOptions"
              option-label="label"
              option-value="value"
              size="small"
              aria-label="Move to layer"
              @update:model-value="(id: string) => editor.moveToLayer([single!.id], id)"
            />
          </section>
        </div>
      </div>
    </template>
  </Drawer>
</template>

<style scoped>
.map-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.head {
  display: flex;
  align-items: flex-start;
  gap: var(--pgpm-space-3);
  padding: var(--pgpm-space-4) var(--pgpm-space-3) var(--pgpm-space-3) var(--pgpm-space-4);
  border-bottom: 1px solid var(--pgpm-border);
}

.title {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  padding-top: 2px;
}

h2 {
  margin: 0;
  font-size: 1.1429rem;
  line-height: 1.3;
  overflow-wrap: anywhere;
}

.sub {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-2);
  font-size: 0.929rem;
}

.avatar {
  flex: none;
  display: inline-grid;
  place-items: center;
  width: 2.75rem;
  height: 2.75rem;
  border-radius: 50%;
  border: 2px solid color-mix(in srgb, var(--avatar) 70%, var(--pgpm-text));
  background: var(--avatar);
  color: var(--pgpm-text);
  font-size: 1.071rem;
}

.status {
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid var(--status);
  background: color-mix(in srgb, var(--status) 16%, var(--pgpm-panel));
}

.overdue {
  padding: 0 6px;
  border-radius: 999px;
  color: var(--pgpm-fail);
  background: var(--pgpm-fail-soft);
  font-weight: 600;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-2) var(--pgpm-space-4);
  border-bottom: 1px solid var(--pgpm-border);
}

.group {
  display: inline-flex;
}

.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-6);
  padding: var(--pgpm-space-4) var(--pgpm-space-4) calc(var(--pgpm-space-6) + env(safe-area-inset-bottom, 0px));
}

.body :deep(section) {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.body :deep(h3) {
  margin: 0;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.body :deep(.count) {
  margin-left: 0.25rem;
  font-weight: 500;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}
</style>

<style>
/* PrimeVue's own rule (.p-drawer-right .p-drawer: 20rem) weighs as much as a two-class one: one more (PNMTD). */
.p-drawer-mask .p-drawer.map-drawer {
  width: min(30rem, 100vw);
}

@media (max-width: 599px) {
  .p-drawer-mask .p-drawer.map-drawer {
    width: 100vw;
  }
}
</style>
