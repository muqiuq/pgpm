<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { useNotify } from '@/composables/useNotify'
import { repo } from '@/local/repo'
import type { PersonRow } from '@/local/rows'

/**
 * A manual person, new or changed (docs/01 §16, *Persons: Edit*): name, e-mail, job, organisation, phones, notes –
 * written to the local data (offline too) and synced. A Microsoft person's directory fields belong to the directory sync
 * and are shown read-only; only the notes are PGPM's own.
 */
const props = defineProps<{ person: PersonRow | null }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ saved: [id: string] }>()
const notify = useNotify()

type Field =
  | 'displayName'
  | 'givenName'
  | 'surname'
  | 'email'
  | 'jobTitle'
  | 'department'
  | 'companyName'
  | 'phone'
  | 'mobilePhone'
  | 'notes'

const fields: { key: Exclude<Field, 'notes'>; label: string; wide?: boolean; type?: string }[] = [
  { key: 'displayName', label: 'Name', wide: true },
  { key: 'givenName', label: 'Given name' },
  { key: 'surname', label: 'Surname' },
  { key: 'email', label: 'E-mail', type: 'email' },
  { key: 'jobTitle', label: 'Job title' },
  { key: 'department', label: 'Department' },
  { key: 'companyName', label: 'Company' },
  { key: 'phone', label: 'Phone', type: 'tel' },
  { key: 'mobilePhone', label: 'Mobile phone', type: 'tel' },
]

const draft = ref<Record<Field, string>>(empty())
const errors = ref<Partial<Record<Field, string>>>({})
const saving = ref(false)

const directory = computed(() => props.person?.source === 'microsoft')
/** A user's own person: name and e-mail are the account's (the server refuses to patch them). */
const ofUser = computed(() => !directory.value && !!props.person?.userId)
const locked = (key: Field) =>
  key !== 'notes' && (directory.value || (ofUser.value && (key === 'displayName' || key === 'email')))
const title = computed(() => (props.person ? `Edit ${props.person.displayName}` : 'New person'))

function empty(): Record<Field, string> {
  return {
    displayName: '',
    givenName: '',
    surname: '',
    email: '',
    jobTitle: '',
    department: '',
    companyName: '',
    phone: '',
    mobilePhone: '',
    notes: '',
  }
}

watch(
  visible,
  (open) => {
    if (!open) return
    const p = props.person
    const next = empty()
    if (p) for (const key of Object.keys(next) as Field[]) next[key] = (p[key] as string | null) ?? ''
    draft.value = next
    errors.value = {}
  },
  { immediate: true },
)

/** The draft as row values: trimmed, empty → null, the e-mail lower-cased (data model §5). */
function values(): Record<Field, string | null> {
  const out = {} as Record<Field, string | null>
  for (const [key, value] of Object.entries(draft.value) as [Field, string][]) {
    const text = key === 'notes' ? value : value.trim()
    out[key] = text.trim() ? (key === 'email' ? text.toLowerCase() : text) : null
  }
  return out
}

function validate(next: Record<Field, string | null>): boolean {
  const found: Partial<Record<Field, string>> = {}
  if (!locked('displayName')) {
    if (!next.displayName) found.displayName = 'A name is needed.'
    else if (next.displayName.length > 200) found.displayName = 'At most 200 characters.'
  }
  if (!locked('email') && next.email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(next.email))
    found.email = 'An e-mail address.'
  errors.value = found
  return !Object.keys(found).length
}

async function save() {
  const next = values()
  if (!validate(next)) return
  saving.value = true
  try {
    if (!props.person) {
      const created = await repo.create('persons', { source: 'manual', isActive: true, ...next })
      notify.success('Person created')
      visible.value = false
      emit('saved', created.id)
      return
    }
    const changes = Object.fromEntries(Object.entries(next).filter(([key]) => !locked(key as Field)))
    await repo.patch('persons', props.person.id, changes)
    notify.success('Person saved')
    visible.value = false
    emit('saved', props.person.id)
  } catch (e) {
    notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="title" modal :style="{ width: 'min(40rem, 100vw)' }">
    <form id="person-form" class="form" novalidate @submit.prevent="save">
      <Message v-if="directory" severity="secondary" size="small" :closable="false">
        From Microsoft Entra: the directory sync keeps these fields. Only the notes can be changed here.
      </Message>
      <Message v-else-if="ofUser" severity="secondary" size="small" :closable="false">
        This person is a user’s: the name and e-mail come from the user account.
      </Message>
      <div class="form-grid">
        <div v-for="f in fields" :key="f.key" class="field" :class="{ wide: f.wide }">
          <label :for="`person-${f.key}`">{{ f.label }}</label>
          <InputText
            :id="`person-${f.key}`"
            v-model="draft[f.key]"
            :type="f.type ?? 'text'"
            :disabled="locked(f.key)"
            :invalid="!!errors[f.key]"
            :autofocus="f.key === 'displayName' && !locked('displayName')"
            maxlength="200"
          />
          <small v-if="errors[f.key]" class="field-error">{{ errors[f.key] }}</small>
        </div>
        <div class="field wide">
          <label for="person-notes">Notes</label>
          <Textarea id="person-notes" v-model="draft.notes" rows="3" auto-resize :autofocus="directory" />
        </div>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="person-form" :label="person ? 'Save' : 'Create person'" :loading="saving" />
    </template>
  </Dialog>
</template>
