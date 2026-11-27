<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { useToast } from 'primevue/usetoast'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { usePreferences } from '@/stores/preferences'
import ColorField from '../chrome/ColorField.vue'
import { CULL_MIN_ELEMENTS, LOD_ZOOM } from '../culling'
import { boundsOf } from '../geometry'
import MapCanvas from '../MapCanvas.vue'
import MatrixMap from '../MatrixMap.vue'
import { createMemorySource, type MemorySource } from '../memorySource'
import type { ImageRequest, ItemRequest, PlaceRequest } from '../tools'
import type { ItemRow } from '../types'
import { runPhase, standardPhases, type PhaseResult } from './benchmark'
import FpsMeter from './FpsMeter.vue'
import type { Pending } from './pending'
import PlaygroundPicker from './PlaygroundPicker.vue'
import { demoCanvas, FIELDS, matrixMap, sampleId, sampleImage, stressMap } from './sampleData'

/**
 * The map UI over the in-memory data source, for checking it by hand and for spike S2 (docs/05 §7): `/dev/map`,
 * registered in development only (router/index.ts). `?map=demo|stress|matrix`, `&focus=item:<id>`, `&bench=1` runs the
 * frame-time benchmark at once (its results also go to `window.__pgpmBench`).
 */
type Choice = 'demo' | 'stress' | 'matrix'
const route = useRoute()
const router = useRouter()
const toast = useToast()
const preferences = usePreferences()

const choice = ref<Choice>((['demo', 'stress', 'matrix'] as const).find((c) => c === route.query.map) ?? 'demo')
const choices = [
  { label: 'Demo canvas', value: 'demo' },
  { label: 'Stress map (1,000)', value: 'stress' },
  { label: 'Matrix (60)', value: 'matrix' },
]
const readonly = ref(false)
const showFps = ref(choice.value === 'stress' || route.query.bench === '1')
const source = shallowRef<MemorySource>(build(choice.value))
const canvas = ref<InstanceType<typeof MapCanvas> | null>(null)
const matrix = ref<InstanceType<typeof MatrixMap> | null>(null)
const pending = ref<Pending | null>(null)
/** `?lod=0` draws full cards at every zoom, `?cull=0` draws every element: the spike's baselines. */
const tuning = {
  lodZoom: route.query.lod === '0' ? 0 : undefined,
  farZoom: route.query.lod === '0' ? 0 : undefined,
  cullFrom: route.query.cull === '0' ? Infinity : undefined,
}

function build(c: Choice): MemorySource {
  return createMemorySource(c === 'stress' ? stressMap(1000) : c === 'matrix' ? matrixMap(60) : demoCanvas())
}

watch(choice, (c) => {
  source.value = build(c)
  results.value = []
  void router.replace({ query: { ...route.query, map: c === 'demo' ? undefined : c } })
})
watch(readonly, (r) => source.value.setAccess(r ? 'read' : 'manage'))

const map = computed(() => source.value.map.value)
const writes = computed(() => source.value.log.value.length)
const last = computed(() => {
  const m = source.value.log.value.at(-1)
  return m ? `${m.op} ${m.table}${m.changes ? ` (${Object.keys(m.changes).join(', ')})` : ''}` : '–'
})
const shownCanvas = computed(() => canvas.value ?? matrix.value?.canvas ?? null)

// --- the host's answers to the tools ---------------------------------------------------------------------------------

function onRequestItem(request: ItemRequest) {
  pending.value = { kind: 'item', request }
}
function onRequestMilestone(request: PlaceRequest) {
  pending.value = { kind: 'milestone', request }
}
function onRequestProject(request: PlaceRequest) {
  pending.value = { kind: 'project', request }
}
function onRequestImage(request: ImageRequest) {
  const id = sampleId(21)
  const file = request.extra.file
  source.value.setFile(id, file ? URL.createObjectURL(file) : sampleImage('New image', Math.floor(Math.random() * 360)))
  request.place(id, { width: 320, height: 200 })
}
function onOpenItem(id: string) {
  const item = source.value.items.value.find((i) => i.id === id)
  toast.add({ severity: 'info', summary: 'Open details', detail: item?.title ?? id, life: 2500 })
}
function onError(error: unknown) {
  toast.add({ severity: 'error', summary: 'Not saved', detail: String(error), life: 4000 })
}

// --- the item panel's quick edit (the host's in phase 5) ------------------------------------------------------------

const editTitle = ref('')
function saveTitle(item: ItemRow) {
  const title = editTitle.value.trim()
  if (title && title !== item.title) source.value.put('items', { ...item, title })
}
function saveColor(item: ItemRow, color: string | null) {
  source.value.put('items', { ...item, color })
}
function valueOf(item: ItemRow, key: string): number | null {
  const def = FIELDS.find((f) => f.key === key)!
  const v = source.value.fieldValues.value.find((f) => f.item_id === item.id && f.field_id === def.id)?.value
  return typeof v === 'number' ? v : null
}
function setValue(item: ItemRow, key: string, value: number | null) {
  const def = FIELDS.find((f) => f.key === key)!
  const row = source.value.fieldValues.value.find((f) => f.item_id === item.id && f.field_id === def.id)
  if (value === null) {
    if (row) source.value.drop('field_values', row.id)
  } else {
    source.value.put('field_values', { id: row?.id ?? sampleId(22), item_id: item.id, field_id: def.id, value })
  }
}

/** Matrix: a random idea gets new values, its card moves. */
function shuffleOne() {
  const ideas = source.value.items.value.filter((i) => i.type === 'idea')
  const item = ideas[Math.floor(Math.random() * ideas.length)]
  if (!item) return
  setValue(item, 'impact_score', Math.round(Math.random() * 100))
  setValue(item, 'feas_data', Math.floor(Math.random() * 6))
  setValue(item, 'feas_skills', Math.floor(Math.random() * 6))
  setValue(item, 'feas_infra', Math.floor(Math.random() * 6))
  shownCanvas.value?.select([`matrix:${item.id}`])
}

// --- spike S2 -------------------------------------------------------------------------------------------------------

const results = ref<PhaseResult[]>([])
const running = ref(false)

async function benchmark() {
  const c = shownCanvas.value
  const svg = document.querySelector('[data-testid="map-canvas"] svg')
  if (!c || !svg || running.value) return
  running.value = true
  results.value = []
  const elements = source.value.elements.value
  window.__pgpmBench = { status: 'running', results: [], elements: elements.length, userAgent: navigator.userAgent }
  const world = boundsOf(elements, { minX: 0, minY: 0, maxX: 1000, maxY: 1000 })
  const rect = svg.getBoundingClientRect()
  for (const phase of standardPhases(world, rect.width, rect.height)) {
    const r = await runPhase(
      phase,
      (v) => c.setView(v),
      () => c.drawnCount,
    )
    results.value = [...results.value, r]
    window.__pgpmBench.results = results.value
  }
  window.__pgpmBench.status = 'done'
  running.value = false
}

let releaseRail: (() => void) | null = null
onMounted(() => {
  releaseRail = preferences.holdRail()
  if (route.query.bench === '1') setTimeout(() => void benchmark(), 800)
})
onBeforeUnmount(() => releaseRail?.())
</script>

<template>
  <div class="playground">
    <div class="bar">
      <SelectButton
        v-model="choice"
        :options="choices"
        option-label="label"
        option-value="value"
        size="small"
        :allow-empty="false"
        aria-label="Map"
      />
      <label class="check"><Checkbox v-model="readonly" binary input-id="pg-readonly" /><span>Read access</span></label>
      <label class="check"><Checkbox v-model="showFps" binary input-id="pg-fps" /><span>FPS</span></label>
      <Button
        label="Run S2 benchmark"
        icon="pi pi-gauge"
        size="small"
        severity="secondary"
        :loading="running"
        @click="benchmark"
      />
      <Button
        v-if="choice === 'matrix'"
        label="Change an idea's values"
        icon="pi pi-sync"
        size="small"
        severity="secondary"
        @click="shuffleOne"
      />
      <span class="muted small">
        {{ map?.name }} · {{ source.elements.value.length }} elements · {{ writes }} writes · last: {{ last }} · LOD
        &lt; {{ LOD_ZOOM * 100 }} %, culling from {{ CULL_MIN_ELEMENTS }}
      </span>
    </div>
    <table v-if="results.length" class="results" data-testid="bench-results">
      <thead>
        <tr>
          <th>Phase</th>
          <th>fps</th>
          <th>p50 ms</th>
          <th>p95 ms</th>
          <th>max ms</th>
          <th>drawn</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in results" :key="r.name">
          <td>{{ r.name }}</td>
          <td>{{ r.fps }}</td>
          <td>{{ r.p50 }}</td>
          <td>{{ r.p95 }}</td>
          <td>{{ r.max }}</td>
          <td>{{ r.drawn }}</td>
        </tr>
      </tbody>
    </table>
    <div class="map">
      <MatrixMap
        v-if="map?.kind === 'matrix'"
        :key="map.id"
        ref="matrix"
        :source="source"
        :focus="typeof route.query.focus === 'string' ? route.query.focus : null"
        @open-item="onOpenItem"
        @request-image="onRequestImage"
        @error="onError"
      >
        <template #item-panel="{ item, canEdit }">
          <section v-if="canEdit" class="quick-edit">
            <h3>Quick edit (playground)</h3>
            <label v-for="key in ['impact_score', 'feas_data', 'feas_skills', 'feas_infra']" :key="key" class="value">
              <span>{{ FIELDS.find((f) => f.key === key)?.label }}</span>
              <InputNumber
                :model-value="valueOf(item, key)"
                size="small"
                :min="0"
                :max="key === 'impact_score' ? 100 : 5"
                show-buttons
                input-class="number"
                @update:model-value="(v: number | null) => setValue(item, key, v)"
              />
            </label>
          </section>
        </template>
        <template #overlay="{ drawn }">
          <FpsMeter
            v-if="showFps"
            :drawn="drawn"
            :total="source.elements.value.length"
            :zoom="matrix?.canvas?.view.k ?? 1"
          />
        </template>
      </MatrixMap>
      <MapCanvas
        v-else-if="map"
        :key="map.id"
        ref="canvas"
        :source="source"
        :tuning="tuning"
        :focus="typeof route.query.focus === 'string' ? route.query.focus : null"
        @request-item="onRequestItem"
        @request-milestone="onRequestMilestone"
        @request-project="onRequestProject"
        @request-image="onRequestImage"
        @open-item="onOpenItem"
        @error="onError"
      >
        <template #item-panel="{ item, canEdit }">
          <section v-if="canEdit" class="quick-edit">
            <h3>Quick edit (playground)</h3>
            <InputText
              :model-value="item.title"
              size="small"
              aria-label="Title"
              @update:model-value="(v: string | undefined) => (editTitle = v ?? '')"
              @keydown.enter="saveTitle(item)"
              @blur="saveTitle(item)"
            />
            <ColorField :model-value="item.color" label="Colour" @update:model-value="(c) => saveColor(item, c)" />
          </section>
        </template>
        <template #overlay="{ drawn }">
          <FpsMeter v-if="showFps" :drawn="drawn" :total="source.elements.value.length" :zoom="canvas?.view.k ?? 1" />
        </template>
      </MapCanvas>
    </div>
    <PlaygroundPicker :pending="pending" :source="source" @done="pending = null" />
  </div>
</template>

<style scoped>
.playground {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
  height: calc(100vh - var(--pgpm-header-height) - 2 * var(--pgpm-space-4));
  min-height: 520px;
}

.bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-3);
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.small {
  font-size: 0.857rem;
}

.map {
  position: relative;
  flex: 1;
  min-height: 0;
}

.results {
  border-collapse: collapse;
  font-size: 0.857rem;
  font-feature-settings: 'tnum';
}

.results th,
.results td {
  padding: 2px 10px;
  border-bottom: 1px solid var(--pgpm-border);
  text-align: right;
}

.results th:first-child,
.results td:first-child {
  text-align: left;
}

.quick-edit {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.value {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
}

.quick-edit :deep(.number) {
  width: 4.5rem;
}
</style>
