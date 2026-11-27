<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { fieldsApi, sectionsApi, type FieldUsage, type SectionItem } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { useCatalogue } from '@/local/catalogue'
import { useGroups, useOnline } from '@/local/lookups'
import type { FieldDefinitionRow, SectionRow } from '@/local/rows'
import FieldDialog from './FieldDialog.vue'
import SectionDialog from './SectionDialog.vue'
import { typeLabel } from './fieldConfig'

/**
 * *Administration → Sections & Fields* (docs/01 §6, *Configuration: Manage*): the catalogue of sections in its order –
 * applies to, default for, collapsed, archived – and the selected section's fields with type and key. New, edit, move
 * up/down, archive/unarchive and (fields) delete go through the configuration endpoints (online only); the rows come
 * back through the sync, so a *Sync now* follows each change. Deleting a field with values names what goes with it
 * first (`GET …/usage`, then `DELETE …?confirm=<values>`).
 */
const notify = useNotify()
const online = useOnline()
const groups = useGroups()
const { catalogue, loaded } = useCatalogue()

const showArchived = ref(false)
const sections = computed(() => catalogue.value.sections)
const shownSections = computed(() => sections.value.filter((s) => showArchived.value || !s.isArchived))
const archivedCount = computed(
  () =>
    sections.value.filter((s) => s.isArchived).length +
    catalogue.value.fields.filter((f) => f.isArchived && !sectionById(f.sectionId)?.isArchived).length,
)
const sectionById = (id: string) => catalogue.value.sectionById.get(id)

const selectedId = ref<string | null>(null)
watch(
  shownSections,
  (list) => {
    if (!list.some((s) => s.id === selectedId.value)) selectedId.value = list[0]?.id ?? null
  },
  { immediate: true },
)
const selected = computed(() => (selectedId.value ? (sectionById(selectedId.value) ?? null) : null))
const allFields = computed(() =>
  selectedId.value ? (catalogue.value.fieldsBySection.get(selectedId.value) ?? []) : [],
)
const shownFields = computed(() => allFields.value.filter((f) => showArchived.value || !f.isArchived))

const typeNames = { idea: 'Ideas', project: 'Projects' } as const
const listText = (list: readonly ('idea' | 'project')[]) => list.map((t) => typeNames[t]).join(', ') || '–'

const busy = ref(false)
async function call(action: () => Promise<unknown>, success: string, failure: string): Promise<boolean> {
  busy.value = true
  try {
    await action()
    notify.success(success)
    void engine.syncNow()
    return true
  } catch (e) {
    notify.error(e, failure)
    return false
  } finally {
    busy.value = false
  }
}

/** Swaps a row with its neighbour in the shown list; the whole order (archived ones too) goes to the server. */
function swapped<T extends { id: string }>(
  all: readonly T[],
  shown: readonly T[],
  row: T,
  step: -1 | 1,
): string[] | null {
  const at = shown.findIndex((r) => r.id === row.id)
  const other = shown[at + step]
  if (!other) return null
  const ids = all.map((r) => r.id)
  const a = ids.indexOf(row.id)
  const b = ids.indexOf(other.id)
  ids[a] = other.id
  ids[b] = row.id
  return ids
}

function moveSection(s: SectionRow, step: -1 | 1) {
  const ids = swapped(sections.value, shownSections.value, s, step)
  if (ids) void call(() => sectionsApi.order(ids), 'Order saved', 'Order not saved')
}

function moveField(f: FieldDefinitionRow, step: -1 | 1) {
  const ids = swapped(allFields.value, shownFields.value, f, step)
  if (ids) void call(() => fieldsApi.order(f.sectionId, ids), 'Order saved', 'Order not saved')
}

function sectionMenu(s: SectionRow): MenuItem[] {
  const disabled = !online.value || busy.value
  return [
    s.isArchived
      ? {
          label: 'Unarchive',
          icon: 'pi pi-replay',
          disabled,
          command: () => void call(() => sectionsApi.unarchive(s.id), `${s.name} is back`, 'Not changed'),
        }
      : {
          label: 'Archive',
          icon: 'pi pi-folder',
          disabled,
          command: () => void call(() => sectionsApi.archive(s.id), `${s.name} is archived`, 'Not archived'),
        },
  ]
}

function fieldMenu(f: FieldDefinitionRow): MenuItem[] {
  const disabled = !online.value || busy.value
  return [
    f.isArchived
      ? {
          label: 'Unarchive',
          icon: 'pi pi-replay',
          disabled,
          command: () => void call(() => fieldsApi.unarchive(f.id), `${f.label} is back`, 'Not changed'),
        }
      : {
          label: 'Archive',
          icon: 'pi pi-folder',
          disabled,
          command: () => void call(() => fieldsApi.archive(f.id), `${f.label} is archived`, 'Not archived'),
        },
    { label: 'Delete…', icon: 'pi pi-trash', disabled, command: () => void askDelete(f) },
  ]
}

// --- dialogs ---
const sectionDialog = ref(false)
const editedSection = ref<SectionRow | null>(null)
function openSection(s: SectionRow | null) {
  editedSection.value = s
  sectionDialog.value = true
}
function sectionSaved(s: SectionItem) {
  selectedId.value = s.id
  void engine.syncNow()
}

const fieldDialog = ref(false)
const editedField = ref<FieldDefinitionRow | null>(null)
function openField(f: FieldDefinitionRow | null) {
  editedField.value = f
  fieldDialog.value = true
}

const deleting = ref<FieldDefinitionRow | null>(null)
const usage = ref<FieldUsage | null>(null)
const deleteError = ref<string | null>(null)
const deleteOpen = ref(false)

async function askDelete(f: FieldDefinitionRow) {
  deleting.value = f
  usage.value = null
  deleteError.value = null
  deleteOpen.value = true
  try {
    usage.value = await fieldsApi.usage(f.id)
  } catch (e) {
    deleteError.value = e instanceof Error ? e.message : String(e)
  }
}

async function confirmDelete() {
  const f = deleting.value
  if (!f || !usage.value) return
  busy.value = true
  try {
    await fieldsApi.remove(f.id, usage.value.values)
    notify.success(`${f.label} deleted`)
    deleteOpen.value = false
    void engine.syncNow()
  } catch (e) {
    if (e instanceof ApiError && e.code === 'confirmation_required') {
      // Values came or went meanwhile: the new count, asked again.
      usage.value = await fieldsApi.usage(f.id).catch(() => usage.value)
      deleteError.value = 'The number of values changed meanwhile – check again.'
    } else if (e instanceof ApiError && e.code === 'used_by_score') {
      deleteError.value = 'A score uses this field: take it out of the score first.'
    } else {
      deleteError.value = e instanceof Error ? e.message : String(e)
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Sections & Fields" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button label="New section" icon="pi pi-plus" size="small" :disabled="!online" @click="openSection(null)" />
      </template>
    </PageHeader>
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: sections and fields can be changed with a connection.
    </Message>
    <div v-if="archivedCount" class="toolbar">
      <span class="switch">
        <ToggleSwitch v-model="showArchived" input-id="sf-archived" />
        <label for="sf-archived">Show archived ({{ archivedCount }})</label>
      </span>
    </div>
    <div class="two-columns">
      <section class="panel" aria-labelledby="sections-title">
        <div id="sections-title" class="panel-header">Sections</div>
        <EmptyState
          v-if="loaded && !shownSections.length"
          icon="pi pi-list-check"
          title="No sections yet."
          text="Sections group the fields of ideas and projects."
        />
        <ol v-else class="rows">
          <li
            v-for="(s, i) in shownSections"
            :key="s.id"
            :class="{ archived: s.isArchived, selected: s.id === selectedId }"
            :data-testid="`section-${s.id}`"
          >
            <span class="order">
              <Button
                icon="pi pi-angle-up"
                text
                size="small"
                severity="secondary"
                :disabled="!online || busy || i === 0"
                :aria-label="`Move ${s.name} up`"
                @click="moveSection(s, -1)"
              />
              <Button
                icon="pi pi-angle-down"
                text
                size="small"
                severity="secondary"
                :disabled="!online || busy || i === shownSections.length - 1"
                :aria-label="`Move ${s.name} down`"
                @click="moveSection(s, 1)"
              />
            </span>
            <button
              type="button"
              class="pick"
              :aria-pressed="s.id === selectedId"
              :aria-label="`Fields of ${s.name}`"
              @click="selectedId = s.id"
            >
              <span class="name strong ellipsis">{{ s.name }}</span>
              <small class="muted ellipsis">
                {{ listText(s.appliesTo)
                }}<template v-if="s.defaultFor.length">
                  · default for {{ listText(s.defaultFor).toLowerCase() }}</template
                >
              </small>
            </button>
            <i v-if="!s.canEdit" v-tooltip.top="'Edited by some groups only'" class="pi pi-lock muted" />
            <Tag v-if="s.collapsedByDefault" value="Collapsed" severity="secondary" />
            <Tag v-if="s.isArchived" value="Archived" severity="secondary" />
            <span class="count muted">{{
              catalogue.fieldsBySection.get(s.id)?.filter((f) => !f.isArchived).length ?? 0
            }}</span>
            <span class="actions">
              <Button
                icon="pi pi-pencil"
                text
                size="small"
                severity="secondary"
                :disabled="!online"
                :aria-label="`Edit ${s.name}`"
                @click="openSection(s)"
              />
              <MoreMenu :items="sectionMenu(s)" :label="`More actions for ${s.name}`" />
            </span>
          </li>
        </ol>
      </section>
      <section class="panel" aria-labelledby="fields-title">
        <div class="panel-header with-action">
          <span id="fields-title">{{ selected ? `Fields of ${selected.name}` : 'Fields' }}</span>
          <Button
            v-if="selected"
            label="New field"
            icon="pi pi-plus"
            size="small"
            text
            :disabled="!online"
            @click="openField(null)"
          />
        </div>
        <EmptyState v-if="!selected" icon="pi pi-list" title="Choose a section." />
        <EmptyState v-else-if="!shownFields.length" icon="pi pi-list" title="No fields in this section yet." />
        <ol v-else class="rows">
          <li
            v-for="(f, i) in shownFields"
            :key="f.id"
            :class="{ archived: f.isArchived }"
            :data-testid="`field-${f.key}`"
          >
            <span class="order">
              <Button
                icon="pi pi-angle-up"
                text
                size="small"
                severity="secondary"
                :disabled="!online || busy || i === 0"
                :aria-label="`Move ${f.label} up`"
                @click="moveField(f, -1)"
              />
              <Button
                icon="pi pi-angle-down"
                text
                size="small"
                severity="secondary"
                :disabled="!online || busy || i === shownFields.length - 1"
                :aria-label="`Move ${f.label} down`"
                @click="moveField(f, 1)"
              />
            </span>
            <span class="field-name">
              <span class="name ellipsis">
                <small v-if="f.config?.subheading" class="muted">{{ f.config.subheading }} · </small>{{ f.label }}
              </span>
              <small class="mono muted ellipsis">{{ f.key }}</small>
            </span>
            <Tag :value="typeLabel(f.type)" severity="secondary" />
            <Tag v-if="f.required" value="Required" severity="info" />
            <Tag v-if="f.isArchived" value="Archived" severity="secondary" />
            <span class="actions">
              <Button
                icon="pi pi-pencil"
                text
                size="small"
                severity="secondary"
                :disabled="!online"
                :aria-label="`Edit ${f.label}`"
                @click="openField(f)"
              />
              <MoreMenu :items="fieldMenu(f)" :label="`More actions for ${f.label}`" />
            </span>
          </li>
        </ol>
      </section>
    </div>
    <SectionDialog
      v-model:visible="sectionDialog"
      :section="editedSection"
      :groups="groups"
      :disabled="!online"
      @saved="sectionSaved"
    />
    <FieldDialog
      v-model:visible="fieldDialog"
      :field="editedField"
      :section-id="selectedId"
      :sections="sections"
      :fields="catalogue.fields"
      :disabled="!online"
      @saved="engine.syncNow()"
    />
    <Dialog
      v-model:visible="deleteOpen"
      :header="deleting ? `Delete field ${deleting.label}?` : 'Delete field'"
      modal
      :style="{ width: 'min(30rem, 100vw)' }"
    >
      <p v-if="!usage && !deleteError" class="muted">Counting its values…</p>
      <template v-if="usage">
        <p data-testid="field-usage">
          {{
            usage.values
              ? `${usage.values} ${usage.values === 1 ? 'value' : 'values'} on ${usage.items} ${usage.items === 1 ? 'item' : 'items'} go with it.`
              : 'No item has a value for it.'
          }}
          <template v-if="usage.templates">
            It is a default in {{ usage.templates }} {{ usage.templates === 1 ? 'template' : 'templates' }}.</template
          >
        </p>
        <Message v-if="usage.scores.length" severity="warn" :closable="false">
          Used by the {{ usage.scores.length === 1 ? 'score' : 'scores' }} {{ usage.scores.join(', ') }}: take it out
          there first – or archive it instead.
        </Message>
      </template>
      <Message v-if="deleteError" severity="error" :closable="false">{{ deleteError }}</Message>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="deleteOpen = false" />
        <Button
          label="Delete"
          severity="danger"
          :loading="busy"
          :disabled="!online || !usage || !!usage.scores.length"
          @click="confirmDelete"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-4);
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 44px;
  padding: 0 var(--pgpm-space-3) 0 var(--pgpm-space-1);
  border-bottom: 1px solid var(--pgpm-border);
}

.rows li:last-child {
  border-bottom: none;
}

.rows li.archived {
  opacity: 0.6;
}

.rows li.selected {
  background: var(--p-highlight-background);
}

.order {
  display: inline-flex;
  flex: none;
}

.pick {
  all: unset;
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
  cursor: pointer;
}

.pick:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.field-name {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.count {
  font-size: 0.857rem;
}

.actions {
  display: inline-flex;
  align-items: center;
  margin-left: auto;
}
</style>
