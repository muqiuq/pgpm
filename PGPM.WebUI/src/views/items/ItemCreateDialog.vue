<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ShareSpec } from '@/api/sync'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { createItem, defaultStatus, selectableStatuses, titleError } from '@/local/items'
import { useGroups, useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { ItemType } from '@/local/rows'
import { useSession } from '@/stores/session'

/**
 * *New note / idea / lead / project* (docs/01 §4.4): a title, the status (ideas, leads, projects: the type's default
 * preselected) and *Share with*, preselected with all groups of the creator at *edit* (docs/01 §18.2). Projects: blank
 * for now – the template picker comes with phase 4 (slot `#template`). Created locally – the item and its shares in one
 * outbox batch, so it works offline – then the item's page opens.
 */
const props = defineProps<{ type: ItemType }>()
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const session = useSession()
const notify = useNotify()
const groups = useGroups()
const statuses = useStatuses()

const info = computed(() => itemTypes[props.type])
const title = ref('')
const shares = ref<ShareSpec[]>([])
const statusId = ref<string | null>(null)
const error = ref<string | null>(null)
const saving = ref(false)
const formId = computed(() => `new-${props.type}`)

const defaults = computed<ShareSpec[]>(() =>
  groups.value.filter((g) => session.groupIds.includes(g.id)).map((g) => ({ groupId: g.id, access: 'edit' })),
)
const options = computed(() => (info.value.hasStatus ? selectableStatuses(statuses.all.value, props.type) : []))
const preselected = computed(() =>
  info.value.hasStatus ? (defaultStatus(statuses.all.value, props.type)?.id ?? null) : null,
)
const noStatus = computed(() => info.value.hasStatus && statuses.loaded.value && !options.value.length)

watch(
  visible,
  (open) => {
    if (!open) return
    title.value = ''
    error.value = null
    shares.value = defaults.value
    statusId.value = preselected.value
  },
  { immediate: true },
)
// The groups and statuses arrive with the first sync: preselect them then, unless changed meanwhile.
watch(defaults, (next, before) => {
  if (visible.value && JSON.stringify(shares.value) === JSON.stringify(before ?? [])) shares.value = next
})
watch(preselected, (next, before) => {
  if (visible.value && (statusId.value === null || statusId.value === before)) statusId.value = next
})

async function create() {
  error.value = titleError(title.value)
  if (error.value) return
  if (info.value.hasStatus && !statusId.value) return
  saving.value = true
  try {
    const item = await repo.transaction((tx) =>
      createItem(tx, { type: props.type, title: title.value, statusId: statusId.value, shares: shares.value }),
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
      <div v-if="info.hasStatus" class="field">
        <label :for="`${formId}-status`">Status</label>
        <Message v-if="noStatus" severity="warn" :closable="false" size="small">
          There are no {{ info.label }} statuses yet: an administrator sets them up under Administration → Statuses.
        </Message>
        <Select
          v-else
          v-model="statusId"
          :input-id="`${formId}-status`"
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
      <div v-if="type === 'project'" class="field">
        <span class="label">Template</span>
        <slot name="template">
          <small class="muted">A blank project. Templates come with phase 4.</small>
        </slot>
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
