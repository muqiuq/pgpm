<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { itemKey, TYPE_ICONS } from './cards'
import type { MapDataSource } from './dataSource'
import MapCanvas from './MapCanvas.vue'
import MatrixBackground from './MatrixBackground.vue'
import { DEFAULT_QUADRANT_LABELS, type Quadrant } from './matrixLayout'
import { MATRIX_LAYER_ID, matrixElements, matrixScene } from './matrixItems'
import { createFieldLookups, type VirtualLayer } from './model'
import { MATRIX_TOOLS, type ImageRequest } from './tools'
import type { LayerRow } from './types'

/**
 * A matrix map (doc 01 §12.8, docs/03 §13.5): the items of `maps.matrix.filter` placed by two field values
 * (matrixLayout.ts) – cards that can't be dragged and move, animated, when a value changes (also from the side panel's
 * quick edit) – over the axes, split lines and quadrant labels; items without values in the tray *Not yet assessed*.
 * Free elements and layers go on top, with the canvas' tools and components (MapCanvas with the cards as a virtual
 * layer); the quadrant is a badge on every card and a filter.
 */
const props = withDefaults(
  defineProps<{
    source: MapDataSource
    focus?: string | null
    readonly?: boolean
    templateNames?: ReadonlyMap<string, string> | null
  }>(),
  { focus: null, readonly: false, templateNames: null },
)
const emit = defineEmits<{
  openItem: [itemId: string]
  requestImage: [request: ImageRequest]
  error: [error: unknown]
}>()

const { defsByKey, valuesByItem } = createFieldLookups(props.source)
const config = computed(() => props.source.map.value?.matrix ?? null)
const scene = computed(() =>
  config.value ? matrixScene(props.source.items.value, config.value, defsByKey.value, valuesByItem.value) : null,
)
const layer = computed<LayerRow>(() => ({
  id: MATRIX_LAYER_ID,
  map_id: props.source.map.value?.id ?? '',
  name: 'Matrix',
  sort_order: '',
  is_visible: true,
  is_locked: true,
}))
const virtual = computed<VirtualLayer | null>(() =>
  scene.value ? { layer: layer.value, elements: matrixElements(scene.value.layout, layer.value.map_id) } : null,
)
const quadrants = computed(() => {
  const out = new Map<string, { quadrant: Quadrant; label: string }>()
  for (const card of scene.value?.layout.cards ?? []) {
    out.set(card.id, {
      quadrant: card.quadrant,
      label: config.value?.quadrants?.[card.quadrant] || DEFAULT_QUADRANT_LABELS[card.quadrant],
    })
  }
  return out
})
const trayOpen = ref(true)
const canvas = ref<InstanceType<typeof MapCanvas> | null>(null)
defineExpose({ canvas, scene })
</script>

<template>
  <MapCanvas
    ref="canvas"
    :source="source"
    :focus="focus"
    :readonly="readonly"
    :tools="MATRIX_TOOLS"
    :virtual="virtual"
    :quadrants="quadrants"
    :template-names="templateNames"
    :world="scene?.layout.world ?? null"
    animated
    @open-item="(id) => emit('openItem', id)"
    @request-image="(r) => emit('requestImage', r)"
    @error="(e) => emit('error', e)"
  >
    <template #background>
      <MatrixBackground v-if="scene && config" :layout="scene.layout" :config="config" />
    </template>
    <template #aside="{ openItem }">
      <aside v-if="scene" class="tray panel" aria-label="Not yet assessed">
        <header>
          <h3>
            Not yet assessed <span class="count">{{ scene.tray.length }}</span>
          </h3>
          <Button
            :icon="trayOpen ? 'pi pi-angle-up' : 'pi pi-angle-down'"
            size="small"
            text
            severity="secondary"
            :aria-label="trayOpen ? 'Fold' : 'Unfold'"
            :aria-expanded="trayOpen"
            @click="trayOpen = !trayOpen"
          />
        </header>
        <template v-if="trayOpen">
          <p v-if="!scene.tray.length" class="muted small">Every item has both values.</p>
          <ul v-else>
            <li v-for="item in scene.tray" :key="item.id">
              <button type="button" class="row" :data-tray-item="item.id" @click="openItem(item.id)">
                <i :class="TYPE_ICONS[item.type]" aria-hidden="true" />
                <span class="key">{{ itemKey(item) }}</span>
                <span class="title">{{ item.title }}</span>
              </button>
            </li>
          </ul>
        </template>
      </aside>
      <p v-if="!config" class="unconfigured panel">This matrix map has no axes configured yet.</p>
    </template>
    <template #item-panel="slot"><slot name="item-panel" v-bind="slot" /></template>
    <template #map-sharing="slot"><slot name="map-sharing" v-bind="slot" /></template>
    <template #overlay="slot"><slot name="overlay" v-bind="slot" /></template>
  </MapCanvas>
</template>

<style scoped>
.tray {
  position: absolute;
  top: 56px;
  right: var(--pgpm-space-2);
  width: 15rem;
  max-height: calc(100% - 140px);
  overflow-y: auto;
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 12%);
  padding: var(--pgpm-space-1) var(--pgpm-space-2) var(--pgpm-space-2);
}

header {
  display: flex;
  align-items: center;
}

h3 {
  flex: 1;
  margin: 0;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.count {
  margin-left: 0.25rem;
  font-weight: 500;
}

ul {
  list-style: none;
  margin: 0;
  padding: 0;
}

.row {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  width: 100%;
  padding: 4px;
  border: 0;
  border-radius: var(--p-border-radius-sm);
  background: none;
  color: var(--pgpm-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.row:hover {
  background: var(--p-content-hover-background);
}

.key {
  color: var(--pgpm-muted);
  font-size: 0.857rem;
  font-feature-settings: 'tnum';
}

.title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.small {
  font-size: 0.857rem;
}

.unconfigured {
  position: absolute;
  top: 56px;
  left: 50%;
  transform: translateX(-50%);
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
}
</style>
