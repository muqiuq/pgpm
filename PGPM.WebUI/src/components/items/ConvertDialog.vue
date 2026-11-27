<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { convertedStatus, convertToProject, defaultStatus, selectableStatuses, titleError } from '@/local/items'
import { useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow } from '@/local/rows'

/**
 * *Convert to project* (docs/01 §8, §9; docs/03 §12.8): a new project from an idea or lead, made on this device – offline
 * too – in one batch, then its page opens. The `#template` slot is phase 4's (templates, sections and field values).
 */
const props = defineProps<{ source: ItemRow }>()
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const notify = useNotify()
const statuses = useStatuses()

const title = ref('')
const statusId = ref<string | null>(null)
const error = ref<string | null>(null)
const saving = ref(false)

const projectStatuses = computed(() => selectableStatuses(statuses.all.value, 'project'))
const next = computed(() => convertedStatus(statuses.all.value, props.source.type))
const label = computed(() => itemTypes[props.source.type].label)

watch(
  visible,
  (open) => {
    if (!open) return
    title.value = props.source.title
    statusId.value = defaultStatus(statuses.all.value, 'project')?.id ?? null
    error.value = null
  },
  { immediate: true },
)
watch(projectStatuses, () => {
  if (visible.value && !statusId.value) statusId.value = defaultStatus(statuses.all.value, 'project')?.id ?? null
})

async function convert() {
  error.value = titleError(title.value)
  if (error.value) return
  saving.value = true
  try {
    const result = await convertToProject(repo, props.source.id, statuses.all.value, {
      title: title.value,
      statusId: statusId.value,
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
        <label id="convert-status-label" for="convert-status">Status</label>
        <Select
          v-model="statusId"
          input-id="convert-status"
          aria-labelledby="convert-status-label"
          :options="projectStatuses"
          option-label="name"
          option-value="id"
          placeholder="No project status yet"
          size="small"
        />
      </div>
      <slot name="template">
        <p class="muted hint">Templates come with phase 4: this is a blank project.</p>
      </slot>
      <div class="carried">
        <span class="label">The project gets</span>
        <ul>
          <li>title, description, colour, cover image and attachments</li>
          <li>sharing, assigned persons and links</li>
          <li>a link to this {{ label }}</li>
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

.carried p,
.hint {
  margin: 0;
}
</style>
