<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ShareSpec } from '@/api/sync'
import TemplatePicker from '@/components/items/TemplatePicker.vue'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { useTemplates } from '@/local/catalogue'
import { defaultStatus, selectableStatuses, titleError, today } from '@/local/items'
import { useGroups, useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { ItemType } from '@/local/rows'
import {
  allowedStatuses,
  createConfiguredItem,
  selectableTemplates,
  templateShares,
  templateStatus,
} from '@/local/templates'
import { useSession } from '@/stores/session'

/**
 * *New note / idea / lead / project* (docs/01 §4.4, §10.2): a title, the status (ideas, leads, projects: the type's
 * default preselected) and *Share with*, preselected with all groups of the creator at *edit* (docs/01 §18.2). Projects
 * pick a template (or *Blank project*): its allowed statuses and default status, its default shares and – for its
 * milestones – the start date (today). Created locally in one outbox batch – the item, its shares, its sections and
 * field defaults (ideas: the sections *default for ideas*), a template's milestones and gates – so it works offline; then
 * the item's page opens.
 */
const props = defineProps<{ type: ItemType }>()
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const session = useSession()
const notify = useNotify()
const groups = useGroups()
const statuses = useStatuses()
const { templates } = useTemplates()

const info = computed(() => itemTypes[props.type])
const title = ref('')
const shares = ref<ShareSpec[]>([])
const statusId = ref<string | null>(null)
const templateId = ref<string | null>(null)
const startDate = ref<string>(today())
const error = ref<string | null>(null)
const saving = ref(false)
const formId = computed(() => `new-${props.type}`)

const pickable = computed(() =>
  props.type === 'project' ? selectableTemplates([...templates.value.byId.values()]) : [],
)
const template = computed(() => (templateId.value ? (templates.value.byId.get(templateId.value) ?? null) : null))
const groupIds = computed(() => new Set(groups.value.map((g) => g.id)))
const defaults = computed<ShareSpec[]>(() => {
  const fromTemplate = templateShares(template.value, groupIds.value)
  if (fromTemplate.length) return fromTemplate
  return groups.value.filter((g) => session.groupIds.includes(g.id)).map((g) => ({ groupId: g.id, access: 'edit' }))
})
const options = computed(() => {
  if (!info.value.hasStatus) return []
  if (props.type === 'project') return allowedStatuses(statuses.all.value, template.value)
  return selectableStatuses(statuses.all.value, props.type)
})
const preselected = computed(() => {
  if (!info.value.hasStatus) return null
  if (props.type === 'project') return templateStatus(statuses.all.value, template.value)?.id ?? null
  return defaultStatus(statuses.all.value, props.type)?.id ?? null
})
const noStatus = computed(() => info.value.hasStatus && statuses.loaded.value && !options.value.length)
const hasMilestones = computed(() => !!template.value?.milestones.length)

watch(
  visible,
  (open) => {
    if (!open) return
    title.value = ''
    error.value = null
    templateId.value = null
    startDate.value = today()
    shares.value = defaults.value
    statusId.value = preselected.value
  },
  { immediate: true },
)
// The groups and statuses arrive with the first sync, a template changes both: preselect them then, unless changed.
watch(defaults, (next, before) => {
  if (visible.value && JSON.stringify(shares.value) === JSON.stringify(before ?? [])) shares.value = next
})
watch(preselected, (next, before) => {
  if (!visible.value) return
  const stillAllowed = options.value.some((s) => s.id === statusId.value)
  if (statusId.value === null || statusId.value === before || !stillAllowed) statusId.value = next
})

async function create() {
  error.value = titleError(title.value)
  if (error.value) return
  if (info.value.hasStatus && !statusId.value) return
  saving.value = true
  try {
    const item = await repo.transaction((tx) =>
      createConfiguredItem(tx, {
        type: props.type,
        title: title.value,
        statusId: statusId.value,
        shares: shares.value,
        templateId: props.type === 'project' ? templateId.value : null,
        startDate: props.type === 'project' && startDate.value ? startDate.value : null,
      }),
    )
    visible.value = false
    await router.push({ name: info.value.page, params: { id: item.id } })
  } catch (e) {
    notify.error(e, `${info.value.title} not created`)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`New ${info.label}`" modal :style="{ width: 'min(30rem, 100vw)' }">
    <form :id="formId" class="form" novalidate @submit.prevent="create">
      <div class="field">
        <label :for="`${formId}-title`">Title</label>
        <InputText :id="`${formId}-title`" v-model="title" autofocus maxlength="300" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div v-if="type === 'project'" class="field">
        <label :for="`${formId}-template`">Template</label>
        <TemplatePicker v-model="templateId" :templates="pickable" :input-id="`${formId}-template`" />
      </div>
      <div v-if="type === 'project'" class="field">
        <label :for="`${formId}-start`">Start date</label>
        <InputText :id="`${formId}-start`" v-model="startDate" type="date" />
        <small v-if="hasMilestones" class="field-hint">The template’s milestones are due relative to it.</small>
      </div>
      <div v-if="info.hasStatus" class="field">
        <label :id="`${formId}-status-label`" :for="`${formId}-status`">Status</label>
        <Message v-if="noStatus" severity="warn" :closable="false" size="small">
          There are no {{ info.label }} statuses yet: an administrator sets them up under Administration → Statuses.
        </Message>
        <Select
          v-else
          v-model="statusId"
          :input-id="`${formId}-status`"
          :aria-labelledby="`${formId}-status-label`"
          :options="options"
          option-label="name"
          option-value="id"
          placeholder="Status"
        >
          <template #option="{ option }">
            <span class="status-option">
              <span class="dot" :style="{ background: option.color }" aria-hidden="true" />{{ option.name }}
            </span>
          </template>
        </Select>
      </div>
      <div class="field">
        <span class="label">Share with</span>
        <ShareEditor v-model="shares" :groups="groups" :id-prefix="`${formId}-share`" />
        <small class="field-hint">
          {{ shares.length ? 'Members of these groups will see it.' : 'Private: only you (and admins) will see it.' }}
          Later changes need a connection.
        </small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" :form="formId" :label="`Create ${info.label}`" :loading="saving" :disabled="noStatus" />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}

.status-option {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
}
</style>
