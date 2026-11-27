<script setup lang="ts">
import { computed } from 'vue'
import { FAR_TEXT_SIZE, type Detail } from '../culling'
import type { Rect } from '../geometry'
import { resolveStyle } from '../styles'
import type { ElementRow } from '../types'

/** A rectangle (rounded with `radius`) or an ellipse with an optional label (doc 01 §12.3). */
const props = withDefaults(defineProps<{ element: ElementRow; frame: Rect; detail: Detail; editing?: boolean }>(), {
  editing: false,
})

const style = computed(() => resolveStyle(props.element))
const lod = computed(() => props.detail !== 'full')
/** Far out, only large text is drawn. */
const readable = computed(() => props.detail !== 'far' || style.value.fontSize >= FAR_TEXT_SIZE)
const radius = computed(() => Math.min(style.value.radius, props.frame.width / 2, props.frame.height / 2))
const justify = computed(() =>
  style.value.align === 'left' ? 'flex-start' : style.value.align === 'right' ? 'flex-end' : 'center',
)
</script>

<template>
  <g
    class="map-element shape"
    :data-element-id="element.id"
    :transform="`translate(${frame.x} ${frame.y})`"
    :opacity="style.opacity"
  >
    <ellipse
      v-if="element.kind === 'ellipse'"
      class="body"
      :cx="frame.width / 2"
      :cy="frame.height / 2"
      :rx="frame.width / 2"
      :ry="frame.height / 2"
      :fill="style.fill"
      :stroke="style.stroke"
      :stroke-width="style.strokeWidth"
      :stroke-dasharray="style.dash"
    />
    <rect
      v-else
      class="body"
      :width="frame.width"
      :height="frame.height"
      :rx="radius"
      :fill="style.fill"
      :stroke="style.stroke"
      :stroke-width="style.strokeWidth"
      :stroke-dasharray="style.dash"
    />
    <template v-if="element.text && !editing && readable">
      <text
        v-if="lod"
        class="lod-label"
        :x="frame.width / 2"
        :y="frame.height / 2"
        text-anchor="middle"
        dominant-baseline="central"
        :fill="style.textColor"
        :font-size="style.fontSize"
      >
        {{ element.text.split('\n')[0] }}
      </text>
      <foreignObject v-else :width="frame.width" :height="frame.height">
        <div
          xmlns="http://www.w3.org/1999/xhtml"
          class="label"
          :style="{
            justifyContent: justify,
            textAlign: style.align,
            color: style.textColor,
            fontSize: `${style.fontSize}px`,
            fontWeight: style.bold ? 700 : 400,
            fontStyle: style.italic ? 'italic' : 'normal',
          }"
        >
          <span>{{ element.text }}</span>
        </div>
      </foreignObject>
    </template>
  </g>
</template>

<style scoped>
.shape {
  cursor: pointer;
}

.label {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  padding: 6px 10px;
  overflow: hidden;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  line-height: 1.3;
  user-select: none;
  pointer-events: none;
}

.lod-label {
  pointer-events: none;
}
</style>
