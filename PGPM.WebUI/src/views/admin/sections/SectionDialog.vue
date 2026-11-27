<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { sectionsApi, type SectionInput, type SectionItem, type SectionLevel } from '@/api/config'
import { useNotify } from '@/composables/useNotify'
import type { GroupRow, SectionItemType, SectionRow } from '@/local/rows'

/**
 * A section's settings (docs/01 §6.1, data model §7.2–§7.3): name, description, the types it applies to and is a default
 * for, collapsed by default, and per group *view* or *edit* – view rows restrict who sees it, edit rows who changes it
 * (docs/03 §15 #47). The groups are not in the synced rows (`section_groups` is server-only): they are read through
 * `GET /api/v1/config/sections/{id}`; when that fails, the groups stay unchanged unless they are replaced here.
 */
const props = withDefaults(
  defineProps<{ section: SectionRow | null; groups: readonly GroupRow[]; disabled?: boolean }>(),
  { disabled: false },
)
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ saved: [section: SectionItem] }>()
const notify = useNotify()

const types: { value: SectionItemType; label: string }[] = [
  { value: 'idea', label: 'Ideas' },
  { value: 'project', label: 'Projects' },
]
const levels: { value: SectionLevel | null; label: string }[] = [
  { value: null, label: 'No restriction' },
  { value: 'view', label: 'View' },
  { value: 'edit', label: 'Edit' },
]

const name = ref('')
const description = ref('')
const appliesTo = ref<SectionItemType[]>([])
const defaultFor = ref<SectionItemType[]>([])
const collapsed = ref(false)
const access = ref<Record<string, SectionLevel | null>>({})
/** `known`: the groups were read (or it is a new section); `unknown`: the server didn't say. */
const accessState = ref<'loading' | 'known' | 'unknown'>('known')
const replaceAccess = ref(false)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)

watch(
  visible,
  async (open) => {
    if (!open) return
    const s = props.section
    name.value = s?.name ?? ''
    description.value = s?.description ?? ''
    appliesTo.value = s ? [...s.appliesTo] : ['idea', 'project']
    defaultFor.value = s ? [...s.defaultFor] : []
    collapsed.value = s?.collapsedByDefault ?? false
    access.value = {}
    replaceAccess.value = false
    errors.value = {}
    if (!s) {
      accessState.value = 'known'
      return
    }
    accessState.value = 'loading'
    try {
      const item = await sectionsApi.get(s.id)
      access.value = Object.fromEntries(item.access.map((a) => [a.groupId, a.level]))
      accessState.value = 'known'
    } catch {
      accessState.value = 'unknown'
    }
  },
  { immediate: true },
)

// Default for ⊆ applies to.
watch(appliesTo, (list) => (defaultFor.value = defaultFor.value.filter((t) => list.includes(t))))

const editingAccess = computed(() => accessState.value === 'known' || replaceAccess.value)
const title = computed(() => (props.section ? `Edit section ${props.section.name}` : 'New section'))
const summary = computed(() => {
  const chosen = Object.values(access.value)
  const view = chosen.some((l) => l === 'view')
  const edit = chosen.some((l) => l === 'edit')
  return [
    view ? 'Only the groups with View or Edit see it.' : 'Everyone with access to the item sees it.',
    edit ? 'Only the groups with Edit change it.' : 'Everyone who sees it and may edit the item changes it.',
  ].join(' ')
})

const toggled = (list: SectionItemType[], type: SectionItemType, on: boolean) =>
  on ? [...new Set([...list, type])] : list.filter((t) => t !== type)
const setApplies = (type: SectionItemType, on: boolean) => (appliesTo.value = toggled(appliesTo.value, type, on))
const setDefault = (type: SectionItemType, on: boolean) => (defaultFor.value = toggled(defaultFor.value, type, on))

function validate(): boolean {
  const next: Record<string, string | undefined> = {}
  const text = name.value.trim()
  if (!text) next.name = 'A name is needed.'
  else if (text.length > 100) next.name = 'At most 100 characters.'
  if (!appliesTo.value.length) next.appliesTo = 'Ideas, projects or both.'
  errors.value = next
  return !Object.values(next).some(Boolean)
}

async function save() {
  if (props.disabled || !validate()) return
  saving.value = true
  const body: SectionInput = {
    name: name.value.trim(),
    description: description.value.trim() || null,
    appliesTo: types.map((t) => t.value).filter((t) => appliesTo.value.includes(t)),
    defaultFor: types.map((t) => t.value).filter((t) => defaultFor.value.includes(t)),
    collapsedByDefault: collapsed.value,
    access: editingAccess.value
      ? Object.entries(access.value)
          .filter((entry): entry is [string, SectionLevel] => !!entry[1])
          .map(([groupId, level]) => ({ groupId, level }))
      : null,
  }
  try {
    const saved = props.section ? await sectionsApi.update(props.section.id, body) : await sectionsApi.create(body)
    notify.success(props.section ? 'Section saved' : 'Section created')
    visible.value = false
    emit('saved', saved)
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'section_in_template')) {
      errors.value = {
        name: e.field('name'),
        description: e.field('description'),
        appliesTo: e.code === 'section_in_template' ? e.message : e.field('appliesTo'),
        defaultFor: e.field('defaultFor'),
        access: e.field('access'),
      }
      if (!Object.values(errors.value).some(Boolean)) notify.error(e, 'Not saved')
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="title" modal :style="{ width: 'min(36rem, 100vw)' }">
    <form id="section-form" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="sc-name">Name</label>
        <InputText id="sc-name" v-model="name" maxlength="100" autofocus :invalid="!!errors.name" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="sc-description">Description</label>
        <Textarea id="sc-description" v-model="description" rows="2" auto-resize maxlength="2000" />
        <small v-if="errors.description" class="field-error">{{ errors.description }}</small>
      </div>
      <div class="form-grid">
        <fieldset class="choices">
          <legend>Applies to</legend>
          <span v-for="t in types" :key="t.value" class="choice">
            <Checkbox
              :model-value="appliesTo.includes(t.value)"
              binary
              :input-id="`sc-applies-${t.value}`"
              @update:model-value="(on: boolean) => setApplies(t.value, on)"
            />
            <label :for="`sc-applies-${t.value}`">{{ t.label }}</label>
          </span>
          <small v-if="errors.appliesTo" class="field-error">{{ errors.appliesTo }}</small>
        </fieldset>
        <fieldset class="choices">
          <legend>Default for new</legend>
          <span v-for="t in types" :key="t.value" class="choice">
            <Checkbox
              :model-value="defaultFor.includes(t.value)"
              binary
              :input-id="`sc-default-${t.value}`"
              :disabled="!appliesTo.includes(t.value)"
              @update:model-value="(on: boolean) => setDefault(t.value, on)"
            />
            <label :for="`sc-default-${t.value}`">{{ t.label }}</label>
          </span>
          <small class="field-hint">Projects: only those without a template.</small>
          <small v-if="errors.defaultFor" class="field-error">{{ errors.defaultFor }}</small>
        </fieldset>
      </div>
      <div class="switch">
        <ToggleSwitch v-model="collapsed" input-id="sc-collapsed" />
        <label for="sc-collapsed">Collapsed by default</label>
      </div>
      <fieldset class="access" data-testid="section-access">
        <legend>Groups</legend>
        <p v-if="accessState === 'loading'" class="muted">Reading the groups…</p>
        <div v-else-if="accessState === 'unknown'" class="mode">
          <span class="choice">
            <RadioButton v-model="replaceAccess" :value="false" input-id="sc-access-keep" />
            <label for="sc-access-keep">Leave the groups as they are</label>
          </span>
          <span class="choice">
            <RadioButton v-model="replaceAccess" :value="true" input-id="sc-access-replace" />
            <label for="sc-access-replace">Replace them with:</label>
          </span>
          <small class="field-hint">This device can’t read who may view or edit the section.</small>
        </div>
        <template v-if="editingAccess && accessState !== 'loading'">
          <p v-if="!groups.length" class="muted">No groups yet.</p>
          <ul v-else class="groups">
            <li v-for="g in groups" :key="g.id">
              <label :for="`sc-group-${g.id}`" class="ellipsis">{{ g.name }}</label>
              <Select
                :model-value="access[g.id] ?? null"
                :input-id="`sc-group-${g.id}`"
                :options="levels"
                option-label="label"
                option-value="value"
                size="small"
                class="level"
                @update:model-value="(l: SectionLevel | null) => (access = { ...access, [g.id]: l })"
              />
            </li>
          </ul>
          <small class="field-hint">{{ summary }}</small>
        </template>
        <small v-if="errors.access" class="field-error">{{ errors.access }}</small>
      </fieldset>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="section-form"
        :label="section ? 'Save' : 'Create section'"
        :loading="saving"
        :disabled="disabled || accessState === 'loading'"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.choices,
.access {
  border: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  min-width: 0;
}

.choices legend,
.access legend {
  font-weight: 500;
  padding: 0;
  margin-bottom: var(--pgpm-space-1);
}

.choice,
.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.mode {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.groups {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  max-height: 16rem;
  overflow-y: auto;
}

.groups li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-3);
}

.level {
  width: 10rem;
  flex: none;
}
</style>
