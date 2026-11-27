<script setup lang="ts">
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import ShareDialog from '@/components/share/ShareDialog.vue'
import { useFilePipeline } from '@/composables/useFileUrl'
import { useNotify } from '@/composables/useNotify'
import { itemRoute } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { useGroups, useOnline } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { ItemShareRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { createLocalSource, type LocalMapSource } from '@/maps/localSource'
import MapCanvas from '@/maps/MapCanvas.vue'
import MatrixMap from '@/maps/MatrixMap.vue'
import { previewScene, renderPreview } from '@/maps/preview'
import type { MatrixSettings } from '@/maps/types'
import { usePreferences } from '@/stores/preferences'
import { useSyncStore } from '@/stores/sync'
import ItemQuickEdit from './ItemQuickEdit.vue'
import LibraryPanel from './LibraryPanel.vue'
import MapItemSections from './MapItemSections.vue'
import MapMilestonePanel from './MapMilestonePanel.vue'
import { ITEM_DRAG_TYPE, KIND_ICONS } from './mapList'
import { useMapRequests } from './mapRequests'
import MatrixConfigDialog from './MatrixConfigDialog.vue'
import PlaceDialog from './PlaceDialog.vue'

/**
 * A map's page (doc 01 §12, docs/03 §13): the host of the canvas or the matrix map over the local database
 * (localSource.ts), keyed by the map's id, with the drawer folded to the rail. It answers the placing tools (create or
 * add an item, a milestone or gate, a project's timeline, an image through the file pipeline), fills the side panel's
 * quick edit (`#item-panel`, with the item's sections), the milestone's slot (its DoD and gate decision) and the sharing
 * (`#map-sharing`), offers the library panel (drag an
 * item onto the canvas), the matrix settings for managers, and `?focus=item:<id>`. With read access everything is
 * read-only. Leaving a map one changed draws its preview for the map list.
 */
const props = defineProps<{ id: string }>()
const route = useRoute()
const router = useRouter()
const preferences = usePreferences()
const notify = useNotify()
const files = useFilePipeline()
const online = useOnline()
const sync = useSyncStore()
const groups = useGroups()

const make = (id: string) => createLocalSource(id, { files })
const source = shallowRef<LocalMapSource>(make(props.id))
const canvas = ref<InstanceType<typeof MapCanvas> | null>(null)
const matrix = ref<InstanceType<typeof MatrixMap> | null>(null)

const map = computed(() => source.value.map.value)
const access = computed(() => source.value.access.value)
const canEdit = computed(() => !!map.value && access.value !== 'read')
const canManage = computed(() => !!map.value && access.value === 'manage')
const focus = computed(() => (typeof route.query.focus === 'string' ? route.query.focus : null))
const shownCanvas = computed(() => canvas.value ?? matrix.value?.canvas ?? null)

const shares = useLive(
  async () =>
    (
      (await localDb().synced('map_shares').where('mapId').equals(props.id).toArray()) as unknown as ItemShareRow[]
    ).filter((s) => !s.deleted),
  [] as ItemShareRow[],
  () => props.id,
)
const shareList = computed(() =>
  shares.value.value.map((s) => ({
    groupId: s.groupId,
    access: s.access,
    name: groups.value.find((g) => g.id === s.groupId)?.name ?? 'Unknown group',
  })),
)
const shareText = computed(() => {
  const list = shareList.value
  if (!list.length) return 'Private'
  return list.length <= 2 ? list.map((s) => s.name).join(', ') : `${list.length} groups`
})
const conflicts = computed(() =>
  sync.conflicts.filter((c) => c.mapId === props.id || (c.table === 'maps' && c.rowId === props.id)),
)

/** How often each item has a card on this map (the dialogs mark them, the library counts). */
const counts = computed(() => {
  const out = new Map<string, number>()
  for (const e of source.value.elements.value) {
    if (e.kind === 'item' && e.item_id) out.set(e.item_id, (out.get(e.item_id) ?? 0) + 1)
  }
  return out
})
/** The quick edit's key fields: the card badges, the matrix' axes and size. */
const fieldKeys = computed(() => {
  const m = map.value
  const keys = [...(m?.settings?.cardBadges ?? [])]
  if (m?.matrix)
    keys.unshift(...[m.matrix.x?.field, m.matrix.y?.field, m.matrix.size?.field].filter((k): k is string => !!k))
  return [...new Set(keys)]
})

// --- the placing tools' requests ------------------------------------------------------------------------------------

const imageInput = ref<HTMLInputElement | null>(null)
let imagePicked: ((file: File | null) => void) | null = null
function pickImage(): Promise<File | null> {
  imagePicked?.(null)
  return new Promise((resolve) => {
    imagePicked = resolve
    const input = imageInput.value
    if (!input) return resolve(null)
    input.value = ''
    input.click()
  })
}
function imageChosen(done: boolean) {
  const file = done ? (imageInput.value?.files?.[0] ?? null) : null
  imagePicked?.(file)
  imagePicked = null
}

const requests = useMapRequests({
  mapId: () => props.id,
  files,
  pickImage,
  onError: (e, summary) => notify.error(e, summary),
})

function openItem(itemId: string) {
  const item = source.value.items.value.find((i) => i.id === itemId)
  if (item) void router.push(itemRoute({ type: item.type, id: item.id }))
}
function onError(error: unknown) {
  notify.error(error, 'Not saved')
}

// --- the library: drag an item onto the canvas ----------------------------------------------------------------------

const libraryOpen = ref(false)
function addFromLibrary(itemId: string) {
  if (!shownCanvas.value?.addItemCard(itemId)) notify.info('Not added', 'Every layer is hidden or locked.')
}
function onDragOver(event: DragEvent) {
  if (canEdit.value && event.dataTransfer?.types.includes(ITEM_DRAG_TYPE)) {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }
}
function onDrop(event: DragEvent) {
  const id = event.dataTransfer?.getData(ITEM_DRAG_TYPE)
  const c = shownCanvas.value
  if (!id || !c || !canEdit.value) return
  event.preventDefault()
  if (!c.addItemCard(id, c.toWorld(event.clientX, event.clientY)))
    notify.info('Not added', 'Every layer is hidden or locked.')
}

// --- sharing, matrix settings -------------------------------------------------------------------------------------

const sharing = ref(false)
const configuring = ref(false)
async function saveMatrix(next: MatrixSettings) {
  const m = map.value
  if (!m) return
  await source.value.patch('maps', m.id, { matrix: { old: m.matrix ?? null, new: next } })
}

// --- the preview (maps.preview), drawn when leaving a map one changed -----------------------------------------------

function savePreview(src: LocalMapSource) {
  const m = src.map.value
  if (!m || src.access.value === 'read' || !online.value) return
  if (src.writes.value === 0 && m.preview) return
  const layout = m.kind === 'matrix' ? matrix.value?.scene?.layout : null
  const scene = previewScene({
    elements: src.elements.value,
    layers: src.layers.value,
    items: new Map(src.items.value.map((i) => [i.id, i])),
    background: m.settings?.background ?? null,
    matrix: layout ? { world: layout.world, quadrants: Object.values(layout.quadrants), cards: layout.cards } : null,
  })
  void renderPreview(scene)
    .then((preview) => (preview && preview !== m.preview ? repo.patch('maps', m.id, { preview }) : undefined))
    .catch(() => undefined)
}

watch(
  () => props.id,
  (id) => {
    const previous = source.value
    savePreview(previous)
    previous.dispose()
    source.value = make(id)
    libraryOpen.value = false
  },
)

let releaseRail: (() => void) | null = null
onMounted(() => (releaseRail = preferences.holdRail()))
onBeforeUnmount(() => {
  releaseRail?.()
  imagePicked?.(null)
  savePreview(source.value)
  source.value.dispose()
})
</script>

<template>
  <div class="map-page">
    <div v-if="!source.loaded.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState
      v-else-if="!map"
      icon="pi pi-eye-slash"
      title="This map is not on this device."
      text="It was deleted, is no longer shared with you, or has not arrived yet."
    >
      <Button label="All maps" size="small" severity="secondary" outlined @click="router.push({ name: 'maps' })" />
    </EmptyState>
    <template v-else>
      <header class="bar">
        <Button
          v-tooltip.bottom="'All maps'"
          icon="pi pi-arrow-left"
          text
          rounded
          size="small"
          severity="secondary"
          aria-label="All maps"
          @click="router.push({ name: 'maps' })"
        />
        <i :class="KIND_ICONS[map.kind]" class="muted" aria-hidden="true" />
        <h1>{{ map.name }}</h1>
        <Tag v-if="map.is_template" value="Template" severity="info" />
        <Tag v-if="access === 'read'" icon="pi pi-lock" value="Read only" severity="secondary" />
        <Tag
          v-if="conflicts.length"
          v-tooltip.bottom="'Open the conflicts'"
          icon="pi pi-exclamation-triangle"
          :value="conflicts.length === 1 ? '1 conflict' : `${conflicts.length} conflicts`"
          severity="warn"
          class="clickable"
          @click="sync.openDialog('conflicts')"
        />
        <span class="spacer" />
        <Button
          v-if="map.kind === 'canvas'"
          label="Library"
          icon="pi pi-book"
          size="small"
          severity="secondary"
          :outlined="!libraryOpen"
          :aria-pressed="libraryOpen"
          @click="libraryOpen = !libraryOpen"
        />
        <Button
          v-if="map.kind === 'matrix' && canManage"
          label="Matrix settings"
          icon="pi pi-sliders-v"
          size="small"
          severity="secondary"
          outlined
          @click="configuring = true"
        />
        <Button
          :label="`Share · ${shareText}`"
          icon="pi pi-users"
          size="small"
          severity="secondary"
          outlined
          @click="sharing = true"
        />
      </header>
      <div class="body">
        <LibraryPanel
          v-if="libraryOpen && map.kind === 'canvas'"
          :counts="counts"
          :can-add="canEdit"
          @add="addFromLibrary"
          @close="libraryOpen = false"
        />
        <div class="stage" data-testid="map-stage" @dragover="onDragOver" @drop="onDrop">
          <MatrixMap
            v-if="map.kind === 'matrix'"
            :key="`matrix:${source.mapId}`"
            ref="matrix"
            :source="source"
            :focus="focus"
            @open-item="openItem"
            @request-image="requests.onRequestImage"
            @error="onError"
          >
            <template #item-panel="{ item }">
              <ItemQuickEdit :item-id="item.id" :field-keys="fieldKeys">
                <template #sections="sections">
                  <MapItemSections :item-id="sections.itemId" :can-edit="sections.canEdit" />
                </template>
              </ItemQuickEdit>
            </template>
            <template #map-sharing>
              <section class="sharing">
                <h3>Sharing</h3>
                <p v-if="!shareList.length" class="muted small">Private: only the owner and admins see it.</p>
                <ul v-else>
                  <li v-for="s in shareList" :key="s.groupId">{{ s.name }} · {{ s.access }}</li>
                </ul>
                <Button label="Share…" icon="pi pi-users" size="small" outlined @click="sharing = true" />
              </section>
            </template>
          </MatrixMap>
          <MapCanvas
            v-else
            :key="`canvas:${source.mapId}`"
            ref="canvas"
            :source="source"
            :focus="focus"
            @request-item="requests.onRequestItem"
            @request-milestone="requests.onRequestMilestone"
            @request-project="requests.onRequestProject"
            @request-image="requests.onRequestImage"
            @open-item="openItem"
            @error="onError"
          >
            <template #item-panel="{ item }">
              <ItemQuickEdit :item-id="item.id" :field-keys="fieldKeys">
                <template #sections="sections">
                  <MapItemSections :item-id="sections.itemId" :can-edit="sections.canEdit" />
                </template>
              </ItemQuickEdit>
            </template>
            <template #milestone-panel="{ milestone }">
              <MapMilestonePanel :milestone-id="milestone.id" />
            </template>
            <template #map-sharing>
              <section class="sharing">
                <h3>Sharing</h3>
                <p v-if="!shareList.length" class="muted small">Private: only the owner and admins see it.</p>
                <ul v-else>
                  <li v-for="s in shareList" :key="s.groupId">{{ s.name }} · {{ s.access }}</li>
                </ul>
                <Button label="Share…" icon="pi pi-users" size="small" outlined @click="sharing = true" />
              </section>
            </template>
          </MapCanvas>
        </div>
      </div>
      <PlaceDialog :requests="requests" :counts="counts" />
      <ShareDialog
        v-model:visible="sharing"
        :map-id="map.id"
        :title="map.name"
        :owner-user-id="map.owner_user_id ?? ''"
        :access="access"
      />
      <MatrixConfigDialog
        v-if="map.kind === 'matrix'"
        v-model:visible="configuring"
        :matrix="map.matrix ?? null"
        :save="saveMatrix"
      />
    </template>
    <input
      ref="imageInput"
      type="file"
      accept="image/png,image/jpeg,image/gif,image/webp"
      class="hidden-input"
      aria-hidden="true"
      tabindex="-1"
      @change="imageChosen(true)"
      @cancel="imageChosen(false)"
    />
  </div>
</template>

<style scoped>
.map-page {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
  height: calc(100vh - var(--pgpm-header-height) - 2 * var(--pgpm-space-4));
  min-height: 480px;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

.bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
}

h1 {
  margin: 0;
  font-size: 1.15rem;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 40vw;
}

.spacer {
  flex: 1;
}

.clickable {
  cursor: pointer;
}

.body {
  display: flex;
  flex: 1;
  min-height: 0;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.stage {
  position: relative;
  flex: 1;
  min-width: 0;
}

.sharing h3 {
  margin: 0 0 var(--pgpm-space-2);
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.sharing ul {
  margin: 0 0 var(--pgpm-space-2);
  padding-left: 1.1rem;
}

.small {
  font-size: 0.857rem;
}

.hidden-input {
  display: none;
}
</style>
