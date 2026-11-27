<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ShareSpec } from '@/api/sync'
import ShareEditor from '@/components/share/ShareEditor.vue'
import { useNotify } from '@/composables/useNotify'
import { useGroups } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { ItemRow } from '@/local/rows'
import { useSession } from '@/stores/session'

/**
 * *New note* (docs/01 §4.4, §7): a title and *Share with*, preselected with all groups of the creator at *edit*
 * (decision default, docs/01 §18.2). Created locally – the item and its shares in one outbox batch, so it works
 * offline – then the note's page opens.
 */
const visible = defineModel<boolean>('visible', { default: false })
const router = useRouter()
const session = useSession()
const notify = useNotify()
const groups = useGroups()

const title = ref('')
const shares = ref<ShareSpec[]>([])
const error = ref<string | null>(null)
const saving = ref(false)

const defaults = computed<ShareSpec[]>(() =>
  groups.value.filter((g) => session.groupIds.includes(g.id)).map((g) => ({ groupId: g.id, access: 'edit' })),
)

watch(
  visible,
  (open) => {
    if (!open) return
    title.value = ''
    error.value = null
    shares.value = defaults.value
  },
  { immediate: true },
)
// The groups arrive with the first sync: preselect them then, unless changed meanwhile.
watch(defaults, (next, before) => {
  if (visible.value && JSON.stringify(shares.value) === JSON.stringify(before ?? [])) shares.value = next
})

async function create() {
  const text = title.value.trim()
  error.value = !text ? 'A title is needed.' : text.length > 300 ? 'At most 300 characters.' : null
  if (error.value) return
  saving.value = true
  try {
    const note = await repo.transaction(async (tx) => {
      const created = await tx.create<ItemRow>('items', { type: 'note', title: text })
      for (const share of shares.value) {
        await tx.create('item_shares', { itemId: created.id, groupId: share.groupId, access: share.access })
      }
      return created
    })
    visible.value = false
    await router.push({ name: 'note', params: { id: note.id } })
  } catch (e) {
    notify.error(e, 'Note not created')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="New note" modal :style="{ width: 'min(30rem, 100vw)' }">
    <form id="new-note" class="form" novalidate @submit.prevent="create">
      <div class="field">
        <label for="nn-title">Title</label>
        <InputText id="nn-title" v-model="title" autofocus maxlength="300" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <span class="label">Share with</span>
        <ShareEditor v-model="shares" :groups="groups" id-prefix="nn-share" />
        <small class="field-hint">
          {{ shares.length ? 'Members of these groups will see it.' : 'Private: only you (and admins) will see it.' }}
          Later changes need a connection.
        </small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="new-note" label="Create note" :loading="saving" />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}
</style>
