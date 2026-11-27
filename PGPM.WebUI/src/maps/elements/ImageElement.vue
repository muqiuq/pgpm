<script setup lang="ts">
import { computed } from 'vue'
import type { Rect } from '../geometry'
import { resolveStyle } from '../styles'
import type { ElementRow } from '../types'

/** An image (doc 01 §12.3, docs/03 §13.1): from the local blob store; resizable, optional border. */
const props = withDefaults(defineProps<{ element: ElementRow; frame: Rect; url?: string | null }>(), { url: null })

const style = computed(() => resolveStyle(props.element))
const clipId = computed(() => `clip-${props.element.id}`)
</script>

<template>
  <g
    class="map-element image"
    :data-element-id="element.id"
    :transform="`translate(${frame.x} ${frame.y})`"
    :opacity="style.opacity"
  >
    <template v-if="url">
      <clipPath v-if="style.radius > 0" :id="clipId">
        <rect :width="frame.width" :height="frame.height" :rx="style.radius" />
      </clipPath>
      <image
        :href="url"
        :width="frame.width"
        :height="frame.height"
        preserveAspectRatio="xMidYMid slice"
        :clip-path="style.radius > 0 ? `url(#${clipId})` : undefined"
      />
    </template>
    <g v-else class="missing">
      <rect :width="frame.width" :height="frame.height" rx="4" />
      <text :x="frame.width / 2" :y="frame.height / 2" text-anchor="middle" dominant-baseline="central">
        Image not on this device
      </text>
    </g>
    <rect
      v-if="style.strokeWidth > 0"
      class="border"
      :width="frame.width"
      :height="frame.height"
      :rx="style.radius"
      :stroke="style.stroke"
      :stroke-width="style.strokeWidth"
      :stroke-dasharray="style.dash"
    />
  </g>
</template>

<style scoped>
.image {
  cursor: pointer;
}

.missing rect {
  fill: var(--pgpm-none-soft);
  stroke: var(--pgpm-border);
  stroke-dasharray: 4 3;
}

.missing text {
  fill: var(--pgpm-muted);
  font-size: 12px;
}

.border {
  fill: none;
}
</style>
