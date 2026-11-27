<script setup lang="ts">
import { computed } from 'vue'
import type { Box, Point } from '../geometry'

/**
 * The minimap (doc 01 §12.2): every element as a small rectangle – one path, rebuilt only when the drawing changes –
 * and the viewport's frame; pressing or dragging on it centres the view there.
 */
const props = defineProps<{ boxes: readonly Box[]; bounds: Box; viewport: Box }>()
const emit = defineEmits<{ centre: [point: Point] }>()

const WIDTH = 180
const HEIGHT = 120

/** The drawing with a margin (not the viewport: panning must not rebuild the path); the frame is clamped to it. */
const world = computed(() => {
  const b = props.bounds
  const m = Math.max(b.maxX - b.minX, b.maxY - b.minY) * 0.1 + 20
  return { minX: b.minX - m, minY: b.minY - m, maxX: b.maxX + m, maxY: b.maxY + m }
})
const scale = computed(() => {
  const w = Math.max(1, world.value.maxX - world.value.minX)
  const h = Math.max(1, world.value.maxY - world.value.minY)
  return Math.min(WIDTH / w, HEIGHT / h)
})
const offset = computed(() => {
  const w = (world.value.maxX - world.value.minX) * scale.value
  const h = (world.value.maxY - world.value.minY) * scale.value
  return { x: (WIDTH - w) / 2, y: (HEIGHT - h) / 2 }
})
const px = (x: number) => Math.round((offset.value.x + (x - world.value.minX) * scale.value) * 10) / 10
const py = (y: number) => Math.round((offset.value.y + (y - world.value.minY) * scale.value) * 10) / 10

const path = computed(() =>
  props.boxes
    .map((b) => {
      const w = Math.max(0.8, px(b.maxX) - px(b.minX))
      const h = Math.max(0.8, py(b.maxY) - py(b.minY))
      return `M${px(b.minX)} ${py(b.minY)}h${w}v${h}h${-w}z`
    })
    .join(''),
)
const clamp = (v: number, max: number) => Math.min(max, Math.max(0, v))
const frame = computed(() => {
  const x1 = clamp(px(props.viewport.minX), WIDTH - 2)
  const y1 = clamp(py(props.viewport.minY), HEIGHT - 2)
  const x2 = clamp(px(props.viewport.maxX), WIDTH)
  const y2 = clamp(py(props.viewport.maxY), HEIGHT)
  return { x: x1, y: y1, width: Math.max(2, x2 - x1), height: Math.max(2, y2 - y1) }
})

let dragging = false
function at(e: PointerEvent): Point {
  const rect = (e.currentTarget as Element).getBoundingClientRect()
  return {
    x: world.value.minX + (e.clientX - rect.left - offset.value.x) / scale.value,
    y: world.value.minY + (e.clientY - rect.top - offset.value.y) / scale.value,
  }
}
function down(e: PointerEvent) {
  e.stopPropagation()
  dragging = true
  try {
    ;(e.currentTarget as Element).setPointerCapture(e.pointerId)
  } catch {
    // Synthetic pointers: centring works without capture.
  }
  emit('centre', at(e))
}
function move(e: PointerEvent) {
  if (dragging) emit('centre', at(e))
}
</script>

<template>
  <svg
    class="minimap panel"
    :width="WIDTH"
    :height="HEIGHT"
    role="img"
    aria-label="Minimap: press to move the view"
    @pointerdown="down"
    @pointermove="move"
    @pointerup="dragging = false"
    @pointercancel="dragging = false"
  >
    <path class="elements" :d="path" />
    <rect class="frame" :x="frame.x" :y="frame.y" :width="frame.width" :height="frame.height" />
  </svg>
</template>

<style scoped>
.minimap {
  display: block;
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 2px 10px rgb(0 0 0 / 10%);
  cursor: crosshair;
  touch-action: none;
}

.elements {
  fill: color-mix(in srgb, var(--pgpm-muted) 45%, transparent);
}

.frame {
  fill: color-mix(in srgb, var(--p-primary-color) 10%, transparent);
  stroke: var(--p-primary-color);
  stroke-width: 1.5;
}
</style>
