<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { templatesApi } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import ColorPicker from '@/components/items/ColorPicker.vue'
import PageHeader from '@/components/PageHeader.vue'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { useCatalogue, useTemplates } from '@/local/catalogue'
import { useGroups, useOnline, useStatuses } from '@/local/lookups'
import FieldValueInput from '../sections/FieldValueInput.vue'
import MilestonesEditor from './MilestonesEditor.vue'
import {
  emptyTemplateForm,
  formFromParts,
  iconName,
  templateErrors,
  templateInput,
  type TemplateForm,
} from './templateForm'

/**
 * A project template (docs/01 §10.2, data model §7.7, *Configuration: Manage*): name, description, colour, icon, the
 * allowed project statuses (none: all) and the default status, the sections in order (those applying to projects),
 * default values of their fields, the default shares of the create dialog, and the milestones and gates with their
 * offsets, DoD criteria and status suggestions. Saved as one complete request (`POST`/`PUT /api/v1/config/templates`,
 * existing milestones keep their ids); later changes don't alter projects made from it. Online only.
 */
const props = defineProps<{ id?: string }>()
const router = useRouter()
const notify = useNotify()
const online = useOnline()
const groups = useGroups()
const statuses = useStatuses()
const { catalogue } = useCatalogue()
const { templates, loaded } = useTemplates()

const form = ref<TemplateForm>(emptyTemplateForm())
const ready = ref(!props.id)
const errors = ref<Record<string, string>>({})
const serverError = ref<string | null>(null)
const saving = ref(false)

const parts = computed(() => (props.id ? templates.value.byId.get(props.id) : undefined))
watch(
  [parts, loaded],
  () => {
    if (ready.value || !parts.value) return
    form.value = formFromParts(parts.value)
    ready.value = true
  },
  { immediate: true },
)
const missing = computed(() => !!props.id && loaded.value && !parts.value)
const title = computed(() => (props.id ? (parts.value?.template.name ?? 'Project template') : 'New project template'))

const projectStatuses = computed(() =>
  statuses.all.value.filter((s) => s.itemType === 'project' && (!s.isArchived || form.value.statusIds.includes(s.id))),
)
const allowed = computed(() =>
  form.value.statusIds.length
    ? projectStatuses.value.filter((s) => form.value.statusIds.includes(s.id))
    : projectStatuses.value.filter((s) => !s.isArchived),
)
const defaultOptions = computed(() => [
  { value: null, label: 'The projects’ default' },
  ...allowed.value.map((s) => ({ value: s.id, label: s.name })),
])

const sectionChoices = computed(() =>
  catalogue.value.sections.filter(
    (s) => !s.isArchived && s.appliesTo.includes('project') && !form.value.sectionIds.includes(s.id),
  ),
)
const sectionName = (id: string) => catalogue.value.sectionById.get(id)?.name ?? 'Unknown section'

function addSection(id: string | null) {
  if (id) form.value = { ...form.value, sectionIds: [...form.value.sectionIds, id] }
}

function moveSection(i: number, step: -1 | 1) {
  const list = [...form.value.sectionIds]
  const j = i + step
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j]!, list[i]!]
  form.value = { ...form.value, sectionIds: list }
}

function removeSection(id: string) {
  const fields = new Set((catalogue.value.fieldsBySection.get(id) ?? []).map((f) => f.id))
  form.value = {
    ...form.value,
    sectionIds: form.value.sectionIds.filter((s) => s !== id),
    // Defaults of its fields go with it.
    fieldDefaults: form.value.fieldDefaults.filter((d) => !fields.has(d.fieldId)),
  }
}

const defaultChoices = computed(() =>
  form.value.sectionIds
    .flatMap((id) => catalogue.value.fieldsBySection.get(id) ?? [])
    .filter(
      (f) =>
        !f.isArchived &&
        f.type !== 'score' &&
        f.type !== 'person' &&
        !form.value.fieldDefaults.some((d) => d.fieldId === f.id),
    )
    .map((f) => ({ value: f.id, label: `${sectionName(f.sectionId)} · ${f.label}` })),
)
const fieldOf = (id: string) => catalogue.value.fieldById.get(id)

function addDefault(fieldId: string | null) {
  const field = fieldId ? fieldOf(fieldId) : undefined
  if (!field) return
  const value = field.type === 'bool' ? true : (field.defaultValue ?? null)
  form.value = { ...form.value, fieldDefaults: [...form.value.fieldDefaults, { fieldId: field.id, value }] }
}

function setDefault(fieldId: string, value: unknown) {
  form.value = {
    ...form.value,
    fieldDefaults: form.value.fieldDefaults.map((d) => (d.fieldId === fieldId ? { fieldId, value } : d)),
  }
}

const removeDefault = (fieldId: string) =>
  (form.value = { ...form.value, fieldDefaults: form.value.fieldDefaults.filter((d) => d.fieldId !== fieldId) })

const iconClass = computed(() => {
  const value = iconName(form.value.icon)
  return value && /^pi-[a-z0-9-]+$/.test(value) ? `pi ${value}` : null
})

function validate(): boolean {
  errors.value = templateErrors(form.value, { fieldById: catalogue.value.fieldById })
  return !Object.keys(errors.value).length
}

async function save() {
  serverError.value = null
  if (!online.value || !validate()) return
  saving.value = true
  try {
    const body = templateInput(form.value)
    const saved = props.id ? await templatesApi.update(props.id, body) : await templatesApi.create(body)
    notify.success(props.id ? 'Template saved' : 'Template created')
    void engine.syncNow()
    if (!props.id) await router.replace({ name: 'template', params: { id: saved.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      const next: Record<string, string> = {}
      for (const [key, messages] of Object.entries(e.errors)) if (messages[0]) next[key] = messages[0]
      errors.value = next
      serverError.value = Object.entries(next)
        .filter(([k]) => !['name', 'description', 'color', 'icon', 'defaultStatusId'].includes(k))
        .map(([k, m]) => `${k}: ${m}`)
        .join(' · ')
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader
      :title="title"
      :crumbs="[{ label: 'Administration' }, { label: 'Project Templates', to: { name: 'templates' } }]"
    >
      <template #actions>
        <Button label="Back" severity="secondary" text size="small" @click="router.push({ name: 'templates' })" />
        <Button
          :label="id ? 'Save' : 'Create template'"
          icon="pi pi-check"
          size="small"
          :loading="saving"
          :disabled="!online || !ready"
          @click="save"
        />
      </template>
    </PageHeader>
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: templates can be changed with a connection.
    </Message>
    <EmptyState
      v-if="missing"
      icon="pi pi-eye-slash"
      title="This template is not on this device."
      text="It was deleted, or has not arrived yet."
    />
    <div v-else-if="!ready" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <form v-else id="template-form" class="two-columns" novalidate @submit.prevent="save">
      <div class="column">
        <section class="panel" aria-labelledby="tp-general">
          <div id="tp-general" class="panel-header">Template</div>
          <div class="panel-body form">
            <div class="field">
              <label for="tp-name">Name</label>
              <InputText id="tp-name" v-model="form.name" maxlength="100" :invalid="!!errors.name" />
              <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
            </div>
            <div class="field">
              <label for="tp-description">Description</label>
              <Textarea id="tp-description" v-model="form.description" rows="3" auto-resize maxlength="2000" />
              <small v-if="errors.description" class="field-error">{{ errors.description }}</small>
            </div>
            <div class="field">
              <span class="label">Colour</span>
              <ColorPicker v-model="form.color" id-prefix="tp-color" />
              <small v-if="errors.color" class="field-error">{{ errors.color }}</small>
            </div>
            <div class="field">
              <label for="tp-icon">Icon</label>
              <div class="with-preview">
                <i :class="iconClass ?? 'pi pi-briefcase'" aria-hidden="true" />
                <InputText
                  id="tp-icon"
                  v-model="form.icon"
                  class="mono"
                  placeholder="pi-bolt"
                  :invalid="!!errors.icon"
                />
              </div>
              <small v-if="errors.icon" class="field-error">{{ errors.icon }}</small>
            </div>
          </div>
        </section>
        <section class="panel" aria-labelledby="tp-statuses">
          <div id="tp-statuses" class="panel-header">Statuses</div>
          <div class="panel-body form">
            <div class="field">
              <label for="tp-allowed">Allowed statuses</label>
              <MultiSelect
                v-model="form.statusIds"
                input-id="tp-allowed"
                :options="projectStatuses"
                option-label="name"
                option-value="id"
                placeholder="All project statuses"
                display="chip"
              />
              <small class="field-hint">Projects of this template can only use these.</small>
            </div>
            <div class="field">
              <label for="tp-default-status">Default status</label>
              <Select
                v-model="form.defaultStatusId"
                input-id="tp-default-status"
                :options="defaultOptions"
                option-label="label"
                option-value="value"
                :invalid="!!errors.defaultStatusId"
              />
              <small v-if="errors.defaultStatusId" class="field-error">{{ errors.defaultStatusId }}</small>
            </div>
          </div>
        </section>
        <section class="panel" aria-labelledby="tp-shares">
          <div id="tp-shares" class="panel-header">Default shares</div>
          <div class="panel-body">
            <ShareEditor v-model="form.defaultShares" :groups="groups" id-prefix="tp-share" />
            <small class="field-hint">Preselected in the create dialog; sections’ group rights only narrow them.</small>
          </div>
        </section>
      </div>
      <div class="column">
        <section class="panel" aria-labelledby="tp-sections">
          <div id="tp-sections" class="panel-header">Sections</div>
          <div class="panel-body form">
            <ol v-if="form.sectionIds.length" class="rows">
              <li v-for="(sid, i) in form.sectionIds" :key="sid" :data-testid="`tp-section-${sid}`">
                <span class="order">
                  <Button
                    icon="pi pi-angle-up"
                    text
                    size="small"
                    severity="secondary"
                    :disabled="i === 0"
                    :aria-label="`Move ${sectionName(sid)} up`"
                    @click="moveSection(i, -1)"
                  />
                  <Button
                    icon="pi pi-angle-down"
                    text
                    size="small"
                    severity="secondary"
                    :disabled="i === form.sectionIds.length - 1"
                    :aria-label="`Move ${sectionName(sid)} down`"
                    @click="moveSection(i, 1)"
                  />
                </span>
                <span class="ellipsis grow">{{ sectionName(sid) }}</span>
                <Button
                  icon="pi pi-times"
                  text
                  size="small"
                  severity="secondary"
                  :aria-label="`Remove ${sectionName(sid)}`"
                  @click="removeSection(sid)"
                />
              </li>
            </ol>
            <p v-else class="muted none">No sections: projects get the sections that are default for projects.</p>
            <Select
              :model-value="null"
              :options="sectionChoices"
              option-label="name"
              option-value="id"
              placeholder="Add a section…"
              size="small"
              aria-label="Add a section"
              @update:model-value="addSection"
            />
          </div>
        </section>
        <section class="panel" aria-labelledby="tp-defaults">
          <div id="tp-defaults" class="panel-header">Field defaults</div>
          <div class="panel-body form">
            <div
              v-for="d in form.fieldDefaults"
              :key="d.fieldId"
              class="field"
              :data-testid="`tp-default-${d.fieldId}`"
            >
              <label :for="`tp-def-${d.fieldId}`">
                {{
                  fieldOf(d.fieldId)
                    ? `${sectionName(fieldOf(d.fieldId)!.sectionId)} · ${fieldOf(d.fieldId)!.label}`
                    : 'Unknown field'
                }}
              </label>
              <div class="with-remove">
                <FieldValueInput
                  v-if="fieldOf(d.fieldId)"
                  :model-value="d.value"
                  :field="fieldOf(d.fieldId)!"
                  :input-id="`tp-def-${d.fieldId}`"
                  :invalid="!!errors[`fieldDefaults.${d.fieldId}`]"
                  @update:model-value="(v: unknown) => setDefault(d.fieldId, v)"
                />
                <Button
                  icon="pi pi-times"
                  text
                  size="small"
                  severity="secondary"
                  :aria-label="`Remove the default of ${fieldOf(d.fieldId)?.label ?? 'the field'}`"
                  @click="removeDefault(d.fieldId)"
                />
              </div>
              <small v-if="errors[`fieldDefaults.${d.fieldId}`]" class="field-error">{{
                errors[`fieldDefaults.${d.fieldId}`]
              }}</small>
            </div>
            <Select
              :model-value="null"
              :options="defaultChoices"
              option-label="label"
              option-value="value"
              placeholder="Add a default value…"
              filter
              size="small"
              aria-label="Add a default value"
              :disabled="!defaultChoices.length"
              @update:model-value="addDefault"
            />
          </div>
        </section>
        <section class="panel" aria-labelledby="tp-milestones">
          <div id="tp-milestones" class="panel-header">Milestones and gates</div>
          <div class="panel-body">
            <MilestonesEditor v-model="form.milestones" :statuses="allowed" :errors="errors" />
          </div>
        </section>
        <Message v-if="serverError" severity="error" :closable="false">{{ serverError }}</Message>
      </div>
    </form>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
  min-width: 0;
}

.label {
  font-weight: 500;
}

.with-preview,
.with-remove {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.with-remove > :first-child {
  flex: 1;
  min-width: 0;
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
  min-height: 34px;
  border-bottom: 1px solid var(--pgpm-border);
}

.rows li:last-child {
  border-bottom: none;
}

.order {
  display: inline-flex;
  flex: none;
}

.grow {
  flex: 1;
}

.none {
  margin: 0;
}
</style>
