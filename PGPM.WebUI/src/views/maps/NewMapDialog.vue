<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ShareSpec } from '@/api/sync'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { localDb } from '@/local/db'
import { useGroups } from '@/local/lookups'
import { createMap, duplicateMap, mapContent, mapNameError, type MapRecord } from '@/local/maps'
import { repo } from '@/local/repo'
import type { ItemShareRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'
import {
  axisFields,
  canvasTemplates,
  copyName,
  customMatrix,
  matrixPresets,
  type FieldDefinitionRecord,
  type NewMapMode,
} from './mapList'

/**
 * *New map* (doc 01 §12.1, §12.8, §12.9): a canvas (layer *Base*), a matrix map – from a preset (the packages'
 * *Prioritization matrix* first) or with its own two axes –, a map from a canvas template (its layers and drawings), or
 * a *Duplicate* of a map. *Share with* is preselected as for items (the creator's groups at *edit*; a duplicate: the
 * original's sharing). Created on the device – map, layers, elements and shares in one batch, offline too –, then the
 * map opens.
 */
const props = withDefaults(
  defineProps<{
    mode: NewMapMode
    maps: readonly MapRecord[]
    template?: MapRecord | null
    original?: MapRecord | null
  }>(),
  { template: null, original: null },
)
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const session = useSession()
const notify = useNotify()
const groups = useGroups()

const fields = useLive(
  async () =>
    ((await localDb().synced('field_definitions').toArray()) as unknown as FieldDefinitionRecord[]).filter(
      (f) => !f.deleted,
    ),
  [] as FieldDefinitionRecord[],
)
const originalShares = useLive(
  async () =>
    props.original
      ? (
          (await localDb()
            .synced('map_shares')
            .where('mapId')
            .equals(props.original.id)
            .toArray()) as unknown as ItemShareRow[]
        )
          .filter((s) => !s.deleted)
          .map((s) => ({ groupId: s.groupId, access: s.access }))
      : [],
  [] as ShareSpec[],
  () => props.original?.id,
)

const name = ref('')
const error = ref<string | null>(null)
const shares = ref<ShareSpec[]>([])
const saving = ref(false)
const matrixFrom = ref<'preset' | 'axes'>('preset')
const presetId = ref<string | null>(null)
const templateId = ref<string | null>(null)
const xField = ref<string | null>(null)
const yField = ref<string | null>(null)

const presets = computed(() => matrixPresets(props.maps))
const templates = computed(() => canvasTemplates(props.maps))
const axes = computed(() => axisFields(fields.value.value))
const creatorShares = computed<ShareSpec[]>(() =>
  groups.value.filter((g) => session.groupIds.includes(g.id)).map((g) => ({ groupId: g.id, access: 'edit' })),
)
const defaultShares = computed(() => (props.mode === 'duplicate' ? originalShares.value.value : creatorShares.value))

const headers: Record<NewMapMode, string> = {
  canvas: 'New canvas map',
  matrix: 'New matrix map',
  template: 'New map from template',
  duplicate: 'Duplicate map',
}

watch(
  visible,
  (open) => {
    if (!open) return
    error.value = null
    shares.value = defaultShares.value.map((s) => ({ ...s }))
    presetId.value = (props.template?.kind === 'matrix' ? props.template.id : null) ?? presets.value[0]?.id ?? null
    matrixFrom.value = presets.value.length ? 'preset' : 'axes'
    templateId.value = props.template?.id ?? templates.value[0]?.id ?? null
    xField.value = axes.value[0]?.key ?? null
    yField.value = axes.value[1]?.key ?? axes.value[0]?.key ?? null
    name.value =
      props.mode === 'duplicate' && props.original
        ? copyName(props.original.name)
        : props.mode === 'template' && props.template
          ? props.template.name
          : ''
  },
  { immediate: true },
)
// Groups and shares arrive late (the first sync, the live query): preselect them then, unless changed meanwhile.
watch(defaultShares, (next, before) => {
  if (visible.value && JSON.stringify(shares.value) === JSON.stringify(before ?? [])) shares.value = [...next]
})
watch(templateId, (id, before) => {
  const t = templates.value.find((m) => m.id === id)
  const previous = templates.value.find((m) => m.id === before)
  if (t && (!name.value || name.value === previous?.name)) name.value = t.name
})

const blocked = computed(() => {
  if (props.mode === 'template') return !templateId.value
  if (props.mode === 'matrix') return matrixFrom.value === 'preset' ? !presetId.value : !xField.value || !yField.value
  return false
})

async function create() {
  error.value = mapNameError(name.value)
  if (error.value || blocked.value) return
  saving.value = true
  try {
    const db = localDb()
    let map: MapRecord
    if (props.mode === 'duplicate') {
      const content = props.original ? await mapContent(db, props.original.id) : null
      if (!content) throw new Error('The map is not on this device any more.')
      map = await repo.transaction((tx) => duplicateMap(tx, content, { name: name.value, shares: shares.value }))
    } else if (props.mode === 'template' || (props.mode === 'matrix' && matrixFrom.value === 'preset')) {
      const id = props.mode === 'template' ? templateId.value! : presetId.value!
      const template = await mapContent(db, id)
      if (!template) throw new Error('The template is not on this device any more.')
      map = await repo.transaction((tx) =>
        createMap(tx, { kind: template.map.kind, name: name.value, shares: shares.value, template }),
      )
    } else if (props.mode === 'matrix') {
      const x = axes.value.find((a) => a.key === xField.value)!
      const y = axes.value.find((a) => a.key === yField.value)!
      map = await repo.transaction((tx) =>
        createMap(tx, { kind: 'matrix', name: name.value, shares: shares.value, matrix: customMatrix(x, y) }),
      )
    } else {
      map = await repo.transaction((tx) => createMap(tx, { kind: 'canvas', name: name.value, shares: shares.value }))
    }
    visible.value = false
    await router.push({ name: 'map', params: { id: map.id } })
  } catch (e) {
    notify.error(e, 'Map not created')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="headers[mode]" modal :style="{ width: 'min(32rem, 100vw)' }">
    <form id="new-map" class="form" novalidate @submit.prevent="create">
      <div v-if="mode === 'template'" class="field">
        <label for="new-map-template">Template</label>
        <Message v-if="!templates.length" severity="secondary" size="small" :closable="false">
          There are no map templates yet. Any canvas can be marked as a template (Configuration: Manage).
        </Message>
        <Select
          v-else
          v-model="templateId"
          input-id="new-map-template"
          :options="templates"
          option-label="name"
          option-value="id"
        />
        <small class="field-hint">Its layers and drawings are copied, not its item cards.</small>
      </div>
      <template v-if="mode === 'matrix'">
        <SelectButton
          v-model="matrixFrom"
          :options="[
            { label: 'From a preset', value: 'preset' },
            { label: 'Own axes', value: 'axes' },
          ]"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Axes"
        />
        <div v-if="matrixFrom === 'preset'" class="field">
          <label for="new-map-preset">Preset</label>
          <Message v-if="!presets.length" severity="secondary" size="small" :closable="false">
            No matrix presets on this device: import a configuration package, or choose own axes.
          </Message>
          <Select
            v-else
            v-model="presetId"
            input-id="new-map-preset"
            :options="presets"
            option-label="name"
            option-value="id"
          />
          <small v-if="presets.find((p) => p.id === presetId)?.description" class="field-hint">
            {{ presets.find((p) => p.id === presetId)?.description }}
          </small>
        </div>
        <template v-else>
          <Message v-if="!axes.length" severity="warn" size="small" :closable="false">
            There are no number, rating or score fields yet (Administration → Sections &amp; Fields).
          </Message>
          <div class="axes">
            <div class="field">
              <label for="new-map-x">Horizontal axis</label>
              <Select v-model="xField" input-id="new-map-x" :options="axes" option-label="label" option-value="key" />
            </div>
            <div class="field">
              <label for="new-map-y">Vertical axis</label>
              <Select v-model="yField" input-id="new-map-y" :options="axes" option-label="label" option-value="key" />
            </div>
          </div>
          <small class="field-hint">Ideas and projects with both values appear; the rest wait in a tray.</small>
        </template>
      </template>
      <div class="field">
        <label for="new-map-name">Name</label>
        <InputText id="new-map-name" v-model="name" autofocus maxlength="200" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <span class="label">Share with</span>
        <ShareEditor v-model="shares" :groups="groups" id-prefix="new-map-share" />
        <small class="field-hint">
          {{ shares.length ? 'Members of these groups will see it.' : 'Private: only you (and admins) will see it.' }}
          Later changes need a connection.
        </small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="new-map"
        :label="mode === 'duplicate' ? 'Duplicate' : 'Create map'"
        :loading="saving"
        :disabled="blocked"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}

.axes {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pgpm-space-3);
}
</style>
