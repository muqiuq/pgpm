<script setup lang="ts">
import Button from 'primevue/button'

/** Bottom right (docs/03 §13.2): zoom −/level/+, fit, zoom to selection, grid, snap, minimap. */
defineProps<{ zoom: number; grid: boolean; snap: boolean; minimap: boolean; hasSelection: boolean; canSnap: boolean }>()
const emit = defineEmits<{
  zoomIn: []
  zoomOut: []
  reset: []
  fit: []
  fitSelection: []
  grid: []
  snap: []
  minimap: []
}>()
</script>

<template>
  <div class="zoom-controls panel">
    <Button
      v-tooltip.top="'Grid'"
      icon="pi pi-th-large"
      size="small"
      :text="!grid"
      severity="secondary"
      aria-label="Grid"
      :aria-pressed="grid"
      @click="emit('grid')"
    />
    <Button
      v-if="canSnap"
      v-tooltip.top="'Snap to grid'"
      icon="pi pi-thumbtack"
      size="small"
      :text="!snap"
      severity="secondary"
      aria-label="Snap to grid"
      :aria-pressed="snap"
      @click="emit('snap')"
    />
    <Button
      v-tooltip.top="'Minimap'"
      icon="pi pi-map"
      size="small"
      :text="!minimap"
      severity="secondary"
      aria-label="Minimap"
      :aria-pressed="minimap"
      @click="emit('minimap')"
    />
    <span class="separator" />
    <Button icon="pi pi-minus" size="small" text severity="secondary" aria-label="Zoom out" @click="emit('zoomOut')" />
    <button
      v-tooltip.top="'Zoom to 100 %'"
      type="button"
      class="level"
      aria-label="Zoom level, click for 100 %"
      @click="emit('reset')"
    >
      {{ Math.round(zoom * 100) }} %
    </button>
    <Button icon="pi pi-plus" size="small" text severity="secondary" aria-label="Zoom in" @click="emit('zoomIn')" />
    <Button
      v-tooltip.top="'Zoom to fit (F)'"
      icon="pi pi-expand"
      size="small"
      text
      severity="secondary"
      aria-label="Zoom to fit"
      @click="emit('fit')"
    />
    <Button
      v-tooltip.top="'Zoom to selection'"
      icon="pi pi-search-plus"
      size="small"
      text
      severity="secondary"
      aria-label="Zoom to selection"
      :disabled="!hasSelection"
      @click="emit('fitSelection')"
    />
  </div>
</template>

<style scoped>
.zoom-controls {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 2px var(--pgpm-space-1);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 10%);
}

.level {
  min-width: 3.6rem;
  border: 0;
  background: none;
  color: var(--pgpm-text);
  font: inherit;
  font-size: 0.857rem;
  font-feature-settings: 'tnum';
  cursor: pointer;
  border-radius: var(--p-border-radius-sm);
  padding: 4px;
}

.level:hover {
  background: var(--p-content-hover-background);
}

.separator {
  width: 1px;
  align-self: stretch;
  margin: 4px 2px;
  background: var(--pgpm-border);
}
</style>
