<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { TOOL_INFO, type Tool } from '../tools'

/**
 * The tools on the left (doc 01 §12.4), with undo and redo, the layer panel and the map's properties below. A toolbar
 * for the keyboard too: ↑/↓ (and ←/→) move between its buttons, Home/End go to the first/last; Tab leaves it.
 */
const props = defineProps<{
  tools: readonly Tool[]
  canEdit: boolean
  canUndo: boolean
  canRedo: boolean
  undoLabel: string | null
  redoLabel: string | null
  layersOpen: boolean
}>()
const tool = defineModel<Tool>('tool', { required: true })
const emit = defineEmits<{ undo: []; redo: []; layers: []; properties: [] }>()

const shown = computed(() =>
  TOOL_INFO.filter((t) => props.tools.includes(t.tool) && (props.canEdit || t.tool === 'select' || t.tool === 'hand')),
)
const bar = ref<HTMLElement | null>(null)
function onKey(e: KeyboardEvent) {
  const buttons = [...(bar.value?.querySelectorAll<HTMLButtonElement>('button:not([disabled])') ?? [])]
  const at = buttons.indexOf(document.activeElement as HTMLButtonElement)
  if (at < 0) return
  const next =
    e.key === 'ArrowDown' || e.key === 'ArrowRight'
      ? (at + 1) % buttons.length
      : e.key === 'ArrowUp' || e.key === 'ArrowLeft'
        ? (at - 1 + buttons.length) % buttons.length
        : e.key === 'Home'
          ? 0
          : e.key === 'End'
            ? buttons.length - 1
            : -1
  if (next < 0) return
  e.preventDefault()
  buttons[next]!.focus()
}
const mod = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘' : 'Ctrl+'
</script>

<template>
  <div
    ref="bar"
    class="map-toolbar panel"
    role="toolbar"
    aria-label="Map tools"
    aria-orientation="vertical"
    @keydown="onKey"
  >
    <Button
      v-for="t in shown"
      :key="t.tool"
      v-tooltip.right="t.label"
      :icon="t.icon"
      size="small"
      :text="tool !== t.tool"
      :severity="tool === t.tool ? undefined : 'secondary'"
      :aria-label="t.label"
      :aria-pressed="tool === t.tool"
      :data-tool="t.tool"
      @click="tool = t.tool"
    />
    <span class="separator" />
    <template v-if="canEdit">
      <Button
        v-tooltip.right="undoLabel ? `Undo ${undoLabel.toLowerCase()} (${mod}Z)` : 'Undo'"
        icon="pi pi-undo"
        size="small"
        text
        severity="secondary"
        aria-label="Undo"
        :disabled="!canUndo"
        @click="emit('undo')"
      />
      <Button
        v-tooltip.right="redoLabel ? `Redo ${redoLabel.toLowerCase()} (${mod}⇧Z)` : 'Redo'"
        icon="pi pi-refresh"
        size="small"
        text
        severity="secondary"
        aria-label="Redo"
        :disabled="!canRedo"
        @click="emit('redo')"
      />
    </template>
    <Button
      v-tooltip.right="'Layers'"
      icon="pi pi-clone"
      size="small"
      :text="!layersOpen"
      severity="secondary"
      aria-label="Layers"
      :aria-pressed="layersOpen"
      @click="emit('layers')"
    />
    <Button
      v-tooltip.right="'Map properties'"
      icon="pi pi-cog"
      size="small"
      text
      severity="secondary"
      aria-label="Map properties"
      @click="emit('properties')"
    />
  </div>
</template>

<style scoped>
.map-toolbar {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pgpm-space-1);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 10%);
  max-height: 100%;
  overflow-y: auto;
}

.separator {
  height: 1px;
  margin: var(--pgpm-space-1) 2px;
  background: var(--pgpm-border);
}
</style>
