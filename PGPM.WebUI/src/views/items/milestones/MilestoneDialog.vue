<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { ref, watch } from 'vue'
import { useNotify } from '@/composables/useNotify'
import { kindLabels } from '@/domain/milestones'
import { createMilestone, titleError } from '@/local/milestones'
import { repo } from '@/local/repo'
import type { MilestoneKind, MilestoneRow } from '@/local/rows'

/**
 * *New milestone* (docs/01 §11): kind (milestone or gate), title and due date; added at the end of the project's list,
 * on the device (offline too). A gate starts with the decision *pending*.
 */
const props = defineProps<{ itemId: string }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ created: [milestone: MilestoneRow] }>()
const notify = useNotify()

const kinds = (['milestone', 'gate'] as const).map((k) => ({ label: kindLabels[k], value: k }))
const kind = ref<MilestoneKind>('milestone')
const title = ref('')
const dueDate = ref('')
const error = ref<string | null>(null)
const saving = ref(false)

watch(
  visible,
  (open) => {
    if (!open) return
    kind.value = 'milestone'
    title.value = ''
    dueDate.value = ''
    error.value = null
  },
  { immediate: true },
)

async function create() {
  error.value = titleError(title.value)
  if (error.value) return
  saving.value = true
  try {
    const milestone = await createMilestone(repo, props.itemId, {
      kind: kind.value,
      title: title.value,
      dueDate: dueDate.value || null,
    })
    visible.value = false
    emit('created', milestone)
  } catch (e) {
    notify.error(e, 'Not created')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="New milestone" modal :style="{ width: 'min(28rem, 100vw)' }">
    <form id="new-milestone" class="form" novalidate @submit.prevent="create">
      <div class="field">
        <span id="new-milestone-kind" class="label">Kind</span>
        <SelectButton
          v-model="kind"
          :options="kinds"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-labelledby="new-milestone-kind"
        />
        <small class="field-hint">{{
          kind === 'gate'
            ? 'A gate ends with a decision: go, no-go, iterate, pivot or park.'
            : 'A milestone is done when its Definition of Done is.'
        }}</small>
      </div>
      <div class="field">
        <label for="new-milestone-title">Title</label>
        <InputText id="new-milestone-title" v-model="title" autofocus maxlength="300" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <label for="new-milestone-due">Due date</label>
        <InputText id="new-milestone-due" v-model="dueDate" type="date" />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="new-milestone"
        :label="kind === 'gate' ? 'Create gate' : 'Create milestone'"
        :loading="saving"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}
</style>
