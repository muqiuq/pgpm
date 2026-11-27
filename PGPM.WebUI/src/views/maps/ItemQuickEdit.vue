<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import { computed } from 'vue'
import CoverImage from '@/components/files/CoverImage.vue'
import InlineField from '@/components/InlineField.vue'
import AssignmentsPanel from '@/components/items/AssignmentsPanel.vue'
import ColorPicker from '@/components/items/ColorPicker.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import { useNotify } from '@/composables/useNotify'
import { ApiError } from '@/api/client'
import { DEFAULT_RATING_MAX } from '@/domain/score'
import { itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { selectableStatuses } from '@/local/items'
import { useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import { useTemplates } from '@/local/catalogue'
import { setFieldValue } from '@/local/fields'
import { allowsAccess, type FieldDefinitionRow, type ItemRow, type SectionRow, type Stored } from '@/local/rows'
import { allowedStatuses } from '@/local/templates'
import { useLive } from '@/local/useLive'
import { badgeText, type FieldDefinition } from '@/maps/cards'
import { keyFields, type FieldDefinitionRecord } from './mapList'

/**
 * The item card's quick edit in the map's side panel (doc 01 §12.5, the canvas' `#item-panel` slot): title, colour,
 * cover image, status, persons, and the key fields – the card badges and a matrix' axes, scores with the ratings and
 * numbers they come from – each saved at once on the device (a matrix card moves as its values change). The item's own
 * access decides, not the map's; a section that is read-only for the user keeps its fields read-only. The item's
 * sections (phase 4's `ItemSections`) go into the `#sections` slot.
 */
const props = defineProps<{ itemId: string; fieldKeys: readonly string[] }>()
const notify = useNotify()
const statuses = useStatuses()

interface FieldValueRecord {
  id: string
  itemId: string
  fieldId: string
  value: unknown
  deleted?: boolean
}

const item = useLive(
  async () => (await localDb().synced('items').get(props.itemId)) as unknown as Stored<ItemRow> | undefined,
  undefined as Stored<ItemRow> | undefined,
  () => props.itemId,
)
const definitions = useLive(
  async () =>
    ((await localDb().synced('field_definitions').toArray()) as unknown as FieldDefinitionRecord[]).filter(
      (d) => !d.deleted,
    ),
  [] as FieldDefinitionRecord[],
)
const readOnlySections = useLive(
  async () =>
    new Set(
      (await localDb().synced('sections').toArray())
        .filter((s) => s.deleted !== true && s.canEdit === false)
        .map((s) => s.id),
    ),
  new Set<string>(),
)
const values = useLive(
  async () =>
    (
      (await localDb()
        .synced('field_values')
        .where('itemId')
        .equals(props.itemId)
        .toArray()) as unknown as FieldValueRecord[]
    ).filter((v) => !v.deleted),
  [] as FieldValueRecord[],
  () => props.itemId,
)

const current = computed(() => (item.value.value && !item.value.value.deleted ? item.value.value : undefined))
const canEdit = computed(() => !!current.value && allowsAccess(current.value.access, 'edit'))
const info = computed(() => (current.value ? itemTypes[current.value.type] : null))
const status = computed(() => (current.value?.statusId ? statuses.byId.value.get(current.value.statusId) : undefined))
const { templates } = useTemplates()
/** A project's statuses are its template's (as on its page). */
const statusOptions = computed(() => {
  const it = current.value
  if (!it) return []
  if (it.type !== 'project') return selectableStatuses(statuses.all.value, it.type, it.statusId)
  const template = it.projectTemplateId ? templates.value.byId.get(it.projectTemplateId) : null
  return allowedStatuses(statuses.all.value, template, it.statusId)
})

const defsByKey = computed(() => {
  const out: Record<string, FieldDefinition & { sectionId?: string | null }> = {}
  for (const d of definitions.value.value) {
    out[d.key] = {
      id: d.id,
      key: d.key,
      label: d.label,
      type: d.type,
      config: d.config,
      isArchived: d.isArchived,
      sectionId: d.sectionId,
    }
  }
  return out
})
const rowsByField = computed(() => new Map(values.value.value.map((v) => [v.fieldId, v])))
const valuesByKey = computed(() => {
  const out: Record<string, unknown> = {}
  for (const d of definitions.value.value) {
    const row = rowsByField.value.get(d.id)
    if (row) out[d.key] = row.value
  }
  return out
})
const fields = computed(() => keyFields(props.fieldKeys, defsByKey.value).filter((d) => !d.isArchived))
const fieldEditable = (def: FieldDefinition & { sectionId?: string | null }) =>
  canEdit.value && !(def.sectionId && readOnlySections.value.value.has(def.sectionId))

function invalid(field: string, message: string): never {
  throw new ApiError(400, { code: 'validation_failed', errors: { [field]: [message] } })
}

async function saveTitle(value: unknown) {
  const title = String(value ?? '').trim()
  if (!title || title.length > 300) invalid('title', title ? 'At most 300 characters.' : 'A title is needed.')
  await repo.patch('items', props.itemId, { title })
}

async function save(values: Record<string, unknown>, failure: string) {
  try {
    await repo.patch('items', props.itemId, values)
  } catch (e) {
    notify.error(e, failure)
  }
}

/**
 * A field's value, on the device (offline too), as the item page sets it (local/fields.ts): one row per field, inserted
 * once, then patched – null clears it; checked as the server checks it.
 */
async function setValue(def: FieldDefinition, value: unknown) {
  try {
    await repo.transaction(async (tx) => {
      const field = (await tx.db.synced('field_definitions').get(def.id)) as unknown as FieldDefinitionRow | undefined
      if (!field) throw new Error('The field is not on this device (any more).')
      const section = (await tx.db.synced('sections').get(field.sectionId)) as unknown as SectionRow | undefined
      await setFieldValue(tx, props.itemId, field, value ?? null, { section })
    })
  } catch (e) {
    notify.error(e, `${def.label} not saved`)
  }
}

const ratingSteps = (def: FieldDefinition) => {
  const max = typeof def.config?.max === 'number' ? def.config.max : DEFAULT_RATING_MAX
  return Array.from({ length: Math.max(0, Math.floor(max)) + 1 }, (_, i) => i)
}
const optionsOf = (def: FieldDefinition) =>
  (def.config?.options ?? [])
    .filter((o) => !o.archived || o.id === valuesByKey.value[def.key])
    .map((o) => ({
      value: o.id,
      label: o.points !== null && o.points !== undefined ? `${o.label ?? o.id} (${o.points})` : (o.label ?? o.id),
    }))
</script>

<template>
  <div v-if="current && info" class="quick-edit" data-testid="item-quick-edit">
    <section class="form">
      <h3>Quick edit</h3>
      <div class="row">
        <span class="label">Title</span>
        <InlineField :value="current.title" :save="saveTitle" field="title" label="Title" :readonly="!canEdit" />
      </div>
      <div v-if="info.hasStatus" class="row">
        <span class="label">Status</span>
        <Select
          v-if="canEdit"
          :model-value="current.statusId"
          :options="statusOptions"
          option-label="name"
          option-value="id"
          size="small"
          aria-label="Status"
          @update:model-value="
            (id: string | null) => id && id !== current!.statusId && save({ statusId: id }, 'Status not changed')
          "
        >
          <template #value><StatusBadge :status="status" /></template>
          <template #option="{ option }"><StatusBadge :status="option" /></template>
        </Select>
        <StatusBadge v-else :status="status" />
      </div>
      <div class="row column">
        <span class="label">Colour</span>
        <ColorPicker
          :model-value="current.color"
          :disabled="!canEdit"
          id-prefix="quick-color"
          @change="(c: string | null) => save({ color: c }, 'Colour not changed')"
        />
      </div>
    </section>
    <section v-if="fields.length" class="form" aria-label="Key fields">
      <h3>Values</h3>
      <div v-for="def in fields" :key="def.id" class="row" :data-field="def.key">
        <span class="label">{{ def.label }}</span>
        <span v-if="def.type === 'score'" class="score">{{ badgeText(def, defsByKey, valuesByKey) }}</span>
        <span v-else-if="def.type === 'rating'" class="rating" role="group" :aria-label="def.label">
          <button
            v-for="n in ratingSteps(def)"
            :key="n"
            type="button"
            class="step"
            :class="{ on: valuesByKey[def.key] === n }"
            :aria-pressed="valuesByKey[def.key] === n"
            :disabled="!fieldEditable(def)"
            @click="setValue(def, valuesByKey[def.key] === n ? null : n)"
          >
            {{ n }}
          </button>
          <button
            type="button"
            class="step na"
            :class="{ on: valuesByKey[def.key] === 'na' }"
            :aria-pressed="valuesByKey[def.key] === 'na'"
            :disabled="!fieldEditable(def)"
            @click="setValue(def, valuesByKey[def.key] === 'na' ? null : 'na')"
          >
            n/a
          </button>
        </span>
        <InputNumber
          v-else-if="def.type === 'number'"
          :model-value="typeof valuesByKey[def.key] === 'number' ? (valuesByKey[def.key] as number) : null"
          :min="typeof def.config?.min === 'number' ? def.config.min : undefined"
          :max="typeof def.config?.max === 'number' ? def.config.max : undefined"
          :max-fraction-digits="4"
          size="small"
          :disabled="!fieldEditable(def)"
          :input-props="{ 'aria-label': def.label }"
          input-class="number"
          @update:model-value="(v: number | null) => setValue(def, v)"
        />
        <Select
          v-else-if="def.type === 'select'"
          :model-value="valuesByKey[def.key] ?? null"
          :options="optionsOf(def)"
          option-label="label"
          option-value="value"
          show-clear
          size="small"
          :disabled="!fieldEditable(def)"
          :aria-label="def.label"
          @update:model-value="(v: string | null) => setValue(def, v)"
        />
        <span v-else class="score">{{ badgeText(def, defsByKey, valuesByKey) }}</span>
      </div>
    </section>
    <slot name="sections" :item-id="itemId" :can-edit="canEdit" />
    <section class="form">
      <h3>Image</h3>
      <CoverImage :item-id="itemId" :image-file-id="current.imageFileId ?? null" :can-edit="canEdit" />
    </section>
    <section class="form">
      <AssignmentsPanel :item-id="itemId" :can-edit="canEdit" />
    </section>
    <p v-if="!canEdit" class="muted small">
      <i class="pi pi-lock" aria-hidden="true" /> You can read this {{ info.label }}, not change it.
    </p>
  </div>
  <p v-else-if="item.loaded.value" class="muted small">This item isn’t on this device.</p>
  <Button v-else text loading size="small" aria-label="Loading" />
</template>

<style scoped>
.quick-edit {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-4);
}

h3 {
  margin: 0;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.form {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
}

.row.column {
  flex-direction: column;
  align-items: stretch;
}

.label {
  font-size: 0.857rem;
  color: var(--pgpm-muted);
  min-width: 6rem;
}

.score {
  font-feature-settings: 'tnum';
}

.rating {
  display: inline-flex;
  gap: 2px;
}

.step {
  min-width: 1.75rem;
  height: 1.75rem;
  padding: 0 4px;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-sm);
  background: var(--pgpm-panel);
  color: var(--pgpm-text);
  font: inherit;
  font-size: 0.857rem;
  cursor: pointer;
}

.step.on {
  background: var(--p-primary-color);
  border-color: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
}

.step:disabled {
  cursor: default;
  opacity: 0.6;
}

.quick-edit :deep(.number) {
  width: 6rem;
}

.small {
  font-size: 0.857rem;
}
</style>
