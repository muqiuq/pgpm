<script setup lang="ts">
import { computed } from 'vue'
import { FAR_TEXT_SIZE, type Detail } from '../culling'
import type { LineGeometry } from '../scene'
import { resolveStyle } from '../styles'
import type { ElementRow } from '../types'

/**
 * A line or arrow (doc 01 §12.3): straight or elbow, arrow heads, stroke colour, width and style, an optional label.
 * Its geometry comes from the canvas – connected ends follow their elements. A wide transparent path makes it easy to hit.
 */
const props = withDefaults(
  defineProps<{ element: ElementRow; geometry: LineGeometry; detail?: Detail; editing?: boolean }>(),
  { detail: 'full', editing: false },
)

const style = computed(() => resolveStyle(props.element))
</script>

<template>
  <g class="map-element line" :data-element-id="element.id" :opacity="style.opacity">
    <path class="hit" :d="geometry.d" />
    <path
      class="stroke"
      :d="geometry.d"
      :stroke="style.stroke"
      :stroke-width="style.strokeWidth"
      :stroke-dasharray="style.dash"
    />
    <path
      v-for="(head, i) in geometry.heads"
      :key="i"
      class="head"
      :d="head.d"
      :stroke="style.stroke"
      :stroke-width="style.strokeWidth"
      :fill="head.kind === 'arrow' ? 'none' : style.stroke"
    />
    <text
      v-if="element.text && !editing && (detail !== 'far' || style.fontSize >= FAR_TEXT_SIZE)"
      class="label"
      :x="geometry.mid.x"
      :y="geometry.mid.y"
      text-anchor="middle"
      dominant-baseline="central"
      :fill="style.textColor"
      :font-size="style.fontSize"
    >
      {{ element.text }}
    </text>
  </g>
</template>

<style scoped>
.line {
  cursor: pointer;
}

.hit {
  fill: none;
  stroke: transparent;
  stroke-width: 14;
}

.stroke {
  fill: none;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.head {
  stroke-linecap: round;
  stroke-linejoin: round;
}

.label {
  paint-order: stroke;
  stroke: var(--pgpm-panel);
  stroke-width: 4px;
  pointer-events: none;
}
</style>
