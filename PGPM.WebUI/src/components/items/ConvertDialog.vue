<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { useTemplates } from '@/local/catalogue'
import { convertedStatus, convertToProject, titleError, today } from '@/local/items'
import { useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow } from '@/local/rows'
import { allowedStatuses, selectableTemplates, templateStatus } from '@/local/templates'
import TemplatePicker from './TemplatePicker.vue'

/**
 * *Convert to project* (docs/01 §8, §9; docs/03 §12.8): a new project from an idea or lead, made on this device – offline
 * too – in one batch, then its page opens. It asks for a template (or *Blank project*): its allowed statuses, default
 * status and – from the start date – its milestones and gates; the sections and values that apply to projects come
 * along either way.
 */
const props = defineProps<{ source: ItemRow }>()
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const notify = useNotify()
const statuses = useStatuses()
const { templates } = useTemplates()

const title = ref('')
const statusId = ref<string | null>(null)
const templateId = ref<string | null>(null)
const startDate = ref(today())
const error = ref<string | null>(null)
const saving = ref(false)

const pickable = computed(() => selectableTemplates([...templates.value.byId.values()]))
const template = computed(() => (templateId.value ? (templates.value.byId.get(templateId.value) ?? null) : null))
const projectStatuses = computed(() => allowedStatuses(statuses.all.value, template.value))
const preselected = computed(() => templateStatus(statuses.all.value, template.value)?.id ?? null)
const next = computed(() => convertedStatus(statuses.all.value, props.source.type))
const label = computed(() => itemTypes[props.source.type].label)

watch(
  visible,
  (open) => {
    if (!open) return
    title.value = props.source.title
    templateId.value = null
    startDate.value = today()
    statusId.value = preselected.value
    error.value = null
  },
  { immediate: true },
)
watch(preselected, (value, before) => {
  if (!visible.value) return
  const allowed = projectStatuses.value.some((s) => s.id === statusId.value)
  if (!statusId.value || statusId.value === before || !allowed) statusId.value = value
})

async function convert() {
  error.value = titleError(title.value)
  if (error.value) return
  saving.value = true
  try {
    const result = await convertToProject(repo, props.source.id, statuses.all.value, {
      title: title.value,
      statusId: statusId.value,
      templateId: templateId.value,
      startDate: startDate.value || null,
    })
    visible.value = false
    notify.success(
      'Converted to a project',
      result.sourceStatus ? `${itemKey(props.source)} is now ${result.sourceStatus.name}.` : undefined,
    )
    await router.push({ name: 'project', params: { id: result.project.id } })
  } catch (e) {
    notify.error(e, 'Not converted')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="Convert to project" modal :style="{ width: 'min(32rem, 100vw)' }">
    <form id="convert-item" class="form" novalidate @submit.prevent="convert">
      <div class="field">
        <label for="convert-title">Project title</label>
        <InputText id="convert-title" v-model="title" maxlength="300" :invalid="!!error" autofocus />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <label for="convert-template">Template</label>
        <TemplatePicker v-model="templateId" :templates="pickable" input-id="convert-template" />
      </div>
      <div v-if="template?.milestones.length" class="field">
        <label for="convert-start">Start date</label>
        <InputText id="convert-start" v-model="startDate" type="date" />
        <small class="field-hint">The template’s milestones are due relative to it.</small>
      </div>
      <div class="field">
        <label for="convert-status">Status</label>
        <Select
          v-model="statusId"
          input-id="convert-status"
          :options="projectStatuses"
          option-label="name"
          option-value="id"
          placeholder="No project status yet"
          size="small"
        />
      </div>
      <div class="carried">
        <span class="label">The project gets</span>
        <ul>
          <li>title, description, colour and cover image</li>
          <li>sharing, assigned persons and links</li>
          <li v-if="source.type === 'idea'">the sections and field values that apply to projects</li>
          <li v-if="template">the template’s sections, defaults, milestones and gates</li>
          <li>a link to this {{ label }} (its attachments stay here)</li>
        </ul>
        <p class="muted">
          The {{ label }} is kept<template v-if="next">
            and moves to <strong>{{ next.name }}</strong></template
          >.
        </p>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="convert-item" label="Convert" icon="pi pi-arrow-right" :loading="saving" />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}

.carried ul {
  margin: var(--pgpm-space-1) 0;
  padding-left: 1.25rem;
}

.carried p {
  margin: 0;
}
</style>
