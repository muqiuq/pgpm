<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import FilterBar from '@/components/FilterBar.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import ShareDialog from '@/components/share/ShareDialog.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { localDb } from '@/local/db'
import { useUserPersons } from '@/local/lookups'
import { mapNameError, type MapRecord } from '@/local/maps'
import { repo } from '@/local/repo'
import { useLive } from '@/local/useLive'
import { previewSrc } from '@/maps/preview'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import { KIND_ICONS, KIND_LABELS, mapsOfTab, type NewMapMode } from './mapList'
import NewMapDialog from './NewMapDialog.vue'

/**
 * *Work → Maps* (doc 01 §12.1, §12.9): the tabs *Maps* (every map the user can see) and *Templates* (canvas templates
 * and matrix presets) – name, kind, preview, owner, last change – from the device, offline too. *New map* (Maps:
 * Create): a canvas, a matrix map, or one from a template. Per map: open, rename, duplicate, share, mark or unmark as a
 * template (Configuration: Manage), delete (to the Trash, with manage access).
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()
const users = useUserPersons()
const { ask, notify } = useActions()

const maps = useLive(
  async () => ((await localDb().synced('maps').toArray()) as unknown as MapRecord[]).filter((m) => !m.deleted),
  [] as MapRecord[],
)
const tab = computed<'maps' | 'templates'>(() => (route.query.tab === 'templates' ? 'templates' : 'maps'))
const tabs = computed(() => [
  { key: 'maps', label: 'Maps', to: { name: 'maps' } },
  {
    key: 'templates',
    label: 'Templates',
    to: { name: 'maps', query: { tab: 'templates' } },
    badge: mapsOfTab(maps.value.value, 'templates').length || null,
  },
])
const search = ref('')
const shown = computed(() => {
  const words = search.value.trim().toLowerCase().split(/\s+/).filter(Boolean)
  return mapsOfTab(maps.value.value, tab.value).filter((m) => {
    const text = `${m.name} ${m.description ?? ''}`.toLowerCase()
    return words.every((w) => text.includes(w))
  })
})

const canCreate = computed(() => session.allows(['Maps', 'Create']))
const canManageTemplates = computed(() => session.allows(['Configuration', 'Manage']))
const conflicted = computed(() => new Set(sync.conflicts.filter((c) => c.table === 'maps').map((c) => c.rowId)))

// --- new, duplicate ---
const dialog = ref<{ mode: NewMapMode; template: MapRecord | null; original: MapRecord | null } | null>(null)
const dialogOpen = computed({
  get: () => dialog.value !== null,
  set: (open: boolean) => {
    if (!open) dialog.value = null
  },
})
const newMenu = ref<InstanceType<typeof Menu> | null>(null)
const newItems: MenuItem[] = [
  { label: 'Canvas map', icon: KIND_ICONS.canvas, command: () => openNew('canvas') },
  { label: 'Matrix map', icon: KIND_ICONS.matrix, command: () => openNew('matrix') },
  { label: 'From template…', icon: 'pi pi-clone', command: () => openNew('template') },
]
function openNew(mode: NewMapMode, template: MapRecord | null = null, original: MapRecord | null = null) {
  dialog.value = { mode, template, original }
}

// --- rename ---
const renaming = ref<MapRecord | null>(null)
const newName = ref('')
const nameError = ref<string | null>(null)
function startRename(map: MapRecord) {
  renaming.value = map
  newName.value = map.name
  nameError.value = null
}
async function rename() {
  const map = renaming.value
  if (!map) return
  nameError.value = mapNameError(newName.value)
  if (nameError.value) return
  try {
    await repo.patch('maps', map.id, { name: newName.value.trim() })
    renaming.value = null
  } catch (e) {
    notify.error(e, 'Not renamed')
  }
}

// --- share, template, delete ---
const sharing = ref<MapRecord | null>(null)
const sharingOpen = computed({
  get: () => sharing.value !== null,
  set: (open: boolean) => {
    if (!open) sharing.value = null
  },
})

async function setTemplate(map: MapRecord, flag: boolean) {
  try {
    await repo.patch('maps', map.id, { isTemplate: flag })
    notify.success(flag ? `${map.name} is a template now` : `${map.name} is a map again`)
  } catch (e) {
    notify.error(e, 'Not changed')
  }
}

async function remove(map: MapRecord) {
  if (!(await ask(`Delete ${map.name}?`, 'It goes to the Trash, from where it can be restored.', 'Delete'))) return
  try {
    await repo.remove('maps', map.id)
    notify.success('Map deleted', 'It is in the Trash.')
  } catch (e) {
    notify.error(e, 'Not deleted')
  }
}

const canEdit = (m: MapRecord) => m.access === 'edit' || m.access === 'manage'
function actions(map: MapRecord): MenuItem[] {
  const out: MenuItem[] = [{ label: 'Open', icon: 'pi pi-external-link', command: () => open(map) }]
  if (map.isTemplate && canCreate.value) {
    out.push({ label: 'New map from this template', icon: 'pi pi-clone', command: () => newFrom(map) })
  }
  if (canEdit(map)) out.push({ label: 'Rename…', icon: 'pi pi-pencil', command: () => startRename(map) })
  if (canCreate.value && !map.isTemplate) {
    out.push({ label: 'Duplicate…', icon: 'pi pi-copy', command: () => openNew('duplicate', null, map) })
  }
  out.push({ label: 'Share…', icon: 'pi pi-users', command: () => (sharing.value = map) })
  if (canManageTemplates.value && canEdit(map)) {
    out.push(
      map.isTemplate
        ? { label: 'Unmark as template', icon: 'pi pi-times-circle', command: () => setTemplate(map, false) }
        : { label: 'Mark as template', icon: 'pi pi-bookmark', command: () => setTemplate(map, true) },
    )
  }
  if (map.access === 'manage') {
    out.push({ separator: true }, { label: 'Delete…', icon: 'pi pi-trash', command: () => remove(map) })
  }
  return out
}

function newFrom(template: MapRecord) {
  openNew(template.kind === 'matrix' ? 'matrix' : 'template', template)
}
const open = (map: MapRecord) => router.push({ name: 'map', params: { id: map.id } })
</script>

<template>
  <div class="page">
    <PageHeader title="Maps">
      <template #actions>
        <template v-if="canCreate">
          <Button
            label="New map"
            icon="pi pi-plus"
            size="small"
            aria-haspopup="true"
            aria-controls="new-map-menu"
            @click="(e: Event) => newMenu?.toggle(e)"
          />
          <Menu id="new-map-menu" ref="newMenu" :model="newItems" popup />
        </template>
      </template>
    </PageHeader>
    <TabNav :items="tabs" :active="tab" label="Maps" />
    <FilterBar v-model:search="search" :placeholder="tab === 'templates' ? 'Search templates…' : 'Search maps…'" />
    <EmptyState
      v-if="maps.loaded.value && !shown.length"
      :icon="tab === 'templates' ? 'pi pi-clone' : 'pi pi-map'"
      :title="search ? 'Nothing found.' : tab === 'templates' ? 'No templates yet.' : 'No maps yet.'"
      :text="
        tab === 'templates'
          ? 'Any canvas can be marked as a template; configuration packages bring presets.'
          : 'A map lays out ideas, projects, milestones and drawings – freely, or by two scores.'
      "
    />
    <DataTable v-else :value="shown" data-key="id" sort-field="name" :sort-order="1" class="maps" size="small">
      <Column header="" style="width: 7.5rem">
        <template #body="{ data }">
          <button type="button" class="preview" :aria-label="`Open ${data.name}`" @click="open(data)">
            <img v-if="previewSrc(data.preview)" :src="previewSrc(data.preview)!" alt="" />
            <i v-else :class="KIND_ICONS[data.kind as MapRecord['kind']]" aria-hidden="true" />
          </button>
        </template>
      </Column>
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <div class="name">
            <RouterLink :to="{ name: 'map', params: { id: data.id } }" class="strong">{{ data.name }}</RouterLink>
            <i
              v-if="conflicted.has(data.id)"
              v-tooltip.top="'Sync conflict'"
              class="pi pi-exclamation-triangle conflict"
              aria-label="Sync conflict"
            />
            <i
              v-else-if="data._local?.pending"
              v-tooltip.top="'Not synced yet'"
              class="pi pi-cloud-upload muted"
              aria-label="Not synced yet"
            />
          </div>
          <div v-if="data.description" class="muted small ellipsis">{{ data.description }}</div>
        </template>
      </Column>
      <Column field="kind" header="Kind" sortable style="width: 8rem">
        <template #body="{ data }">
          <Tag
            :icon="KIND_ICONS[data.kind as MapRecord['kind']]"
            :value="KIND_LABELS[data.kind as MapRecord['kind']]"
            severity="secondary"
          />
          <Tag v-if="data.access === 'read'" value="Read only" severity="secondary" class="read" />
        </template>
      </Column>
      <Column header="Owner" style="width: 11rem">
        <template #body="{ data }">{{ users.nameOf(data.ownerUserId, '–') }}</template>
      </Column>
      <Column field="updatedAt" header="Modified" sortable style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.updatedAt" /></template>
      </Column>
      <Column header="" style="width: 3.5rem">
        <template #body="{ data }">
          <MoreMenu :items="actions(data)" :label="`Actions for ${data.name}`" />
        </template>
      </Column>
    </DataTable>
    <NewMapDialog
      v-if="dialog"
      v-model:visible="dialogOpen"
      :mode="dialog.mode"
      :maps="maps.value.value"
      :template="dialog.template"
      :original="dialog.original"
    />
    <Dialog
      :visible="renaming !== null"
      header="Rename map"
      modal
      :style="{ width: 'min(28rem, 100vw)' }"
      @update:visible="(v: boolean) => !v && (renaming = null)"
    >
      <form id="rename-map" class="form" novalidate @submit.prevent="rename">
        <div class="field">
          <label for="rename-map-name">Name</label>
          <InputText id="rename-map-name" v-model="newName" autofocus maxlength="200" :invalid="!!nameError" />
          <small v-if="nameError" class="field-error">{{ nameError }}</small>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="renaming = null" />
        <Button type="submit" form="rename-map" label="Rename" />
      </template>
    </Dialog>
    <ShareDialog
      v-if="sharing"
      v-model:visible="sharingOpen"
      :map-id="sharing.id"
      :title="sharing.name"
      :owner-user-id="sharing.ownerUserId"
      :access="sharing.access"
    />
  </div>
</template>

<style scoped>
.preview {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 6.5rem;
  height: 4rem;
  padding: 0;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-sm);
  background: var(--pgpm-panel);
  color: var(--pgpm-muted);
  cursor: pointer;
  overflow: hidden;
}

.preview img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.preview i {
  font-size: 1.4rem;
}

.name {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.conflict {
  color: var(--p-orange-500);
}

.small {
  font-size: 0.857rem;
}

.ellipsis {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 40rem;
}

.read {
  margin-left: var(--pgpm-space-1);
}
</style>
