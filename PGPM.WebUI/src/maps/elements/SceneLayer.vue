<script setup lang="ts">
import type { Rect } from '../geometry'
import type { LineGeometry } from '../scene'
import type { Detail } from '../culling'
import type { SceneLookups } from '../sceneTypes'
import type { ElementRow, LayerRow, MilestoneRow } from '../types'
import ImageElement from './ImageElement.vue'
import ItemCard from './ItemCard.vue'
import LineElement from './LineElement.vue'
import MilestoneMarker from './MilestoneMarker.vue'
import ShapeElement from './ShapeElement.vue'
import TextElement from './TextElement.vue'
import TimelineElement from './TimelineElement.vue'

/**
 * One layer's `<g>` with its elements in drawing order (docs/03 §13.1). Every element gets its own frame object, which
 * stays the same while it doesn't move – so a drag or a selection change re-renders only the elements it touches.
 */
defineProps<{
  layer: LayerRow
  elements: readonly ElementRow[]
  frames: ReadonlyMap<string, Rect>
  lines: ReadonlyMap<string, LineGeometry>
  scene: SceneLookups
  detail: Detail
  selected: ReadonlySet<string>
  editingId: string | null
  animated: boolean
}>()

const NO_MILESTONES: readonly MilestoneRow[] = []
</script>

<template>
  <g class="layer" :class="{ locked: layer.is_locked }" :data-layer-id="layer.id">
    <template v-for="el in elements" :key="el.id">
      <ItemCard
        v-if="el.kind === 'item'"
        :element="el"
        :frame="frames.get(el.id)!"
        :card="(el.item_id && scene.cards.get(el.item_id)) || null"
        :detail="detail"
        :selected="selected.has(el.id)"
        :image-url="el.item_id ? (scene.images.get(scene.cards.get(el.item_id)?.coverFileId ?? '') ?? null) : null"
        :quadrant="(el.item_id && scene.quadrants?.get(el.item_id)) || null"
        :animated="animated"
      />
      <LineElement
        v-else-if="el.kind === 'line' && lines.has(el.id)"
        :element="el"
        :geometry="lines.get(el.id)!"
        :detail="detail"
        :editing="editingId === el.id"
      />
      <ShapeElement
        v-else-if="el.kind === 'rect' || el.kind === 'ellipse'"
        :element="el"
        :frame="frames.get(el.id)!"
        :detail="detail"
        :editing="editingId === el.id"
      />
      <TextElement
        v-else-if="el.kind === 'text'"
        :element="el"
        :frame="frames.get(el.id)!"
        :detail="detail"
        :editing="editingId === el.id"
      />
      <MilestoneMarker
        v-else-if="el.kind === 'milestone'"
        :element="el"
        :frame="frames.get(el.id)!"
        :milestone="(el.milestone_id && scene.milestones.get(el.milestone_id)) || null"
        :detail="detail"
        :now="scene.now"
      />
      <TimelineElement
        v-else-if="el.kind === 'timeline'"
        :element="el"
        :frame="frames.get(el.id)!"
        :project="(el.project_id && scene.items.get(el.project_id)) || null"
        :milestones="(el.project_id && scene.milestonesByProject.get(el.project_id)) || NO_MILESTONES"
        :detail="detail"
      />
      <ImageElement
        v-else-if="el.kind === 'image'"
        :element="el"
        :frame="frames.get(el.id)!"
        :url="el.file_id ? (scene.images.get(el.file_id) ?? null) : null"
      />
    </template>
  </g>
</template>

<style scoped>
.layer.locked :deep(.map-element) {
  cursor: default;
}
</style>
