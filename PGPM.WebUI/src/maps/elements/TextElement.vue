<script setup lang="ts">
import { computed } from 'vue'
import { FAR_TEXT_SIZE, type Detail } from '../culling'
import type { Rect } from '../geometry'
import { resolveStyle } from '../styles'
import type { ElementRow } from '../types'

/** A text (doc 01 §12.3): font size, bold, italic, colour, alignment, optional background (`fill`). */
const props = withDefaults(defineProps<{ element: ElementRow; frame: Rect; detail: Detail; editing?: boolean }>(), {
  editing: false,
})

const style = computed(() => resolveStyle(props.element))
const lod = computed(() => props.detail !== 'full')
/** Far out, only large text is drawn. */
const readable = computed(() => props.detail !== 'far' || style.value.fontSize >= FAR_TEXT_SIZE)
</script>

<template>
  <g
    class="map-element text"
    :data-element-id="element.id"
    :transform="`translate(${frame.x} ${frame.y})`"
    :opacity="style.opacity"
  >
    <rect class="hit" :width="frame.width" :height="frame.height" :fill="style.fill" rx="4" />
    <template v-if="!editing && readable">
      <text
        v-if="lod"
        :x="4"
        :y="style.fontSize"
        :fill="style.textColor"
        :font-size="style.fontSize"
        :font-weight="style.bold ? 700 : 400"
      >
        {{ (element.text ?? '').split('\n')[0] }}
      </text>
      <foreignObject v-else :width="frame.width" :height="frame.height">
        <div
          xmlns="http://www.w3.org/1999/xhtml"
          class="content"
          :class="{ empty: !element.text }"
          :style="{
            textAlign: style.align,
            color: style.textColor,
            fontSize: `${style.fontSize}px`,
            fontWeight: style.bold ? 700 : 400,
            fontStyle: style.italic ? 'italic' : 'normal',
          }"
        >
          {{ element.text || 'Text' }}
        </div>
      </foreignObject>
    </template>
  </g>
</template>

<style scoped>
.text {
  cursor: pointer;
}

.content {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  padding: 2px 4px;
  overflow: hidden;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  line-height: 1.3;
  user-select: none;
  pointer-events: none;
}

.content.empty {
  opacity: 0.45;
}
</style>
