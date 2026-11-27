<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import { useConfirm } from 'primevue/useconfirm'
import { computed, ref, watch } from 'vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { bulkAssign, bulkPatch, bulkRemove, selectableStatuses, type BulkResult } from '@/local/items'
import { useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { ItemType } from '@/local/rows'
import ColorPicker from './ColorPicker.vue'
import PersonPicker from './PersonPicker.vue'
import RoleInput from './RoleInput.vue'

/**
 * The bulk actions of a list (docs/01 §4.3) on the picked items: assign a person (with a role), change the status, change
 * the colour, delete (to the Trash). Each is one outbox batch, offline too; items the user may not change are skipped
 * and counted. *Add to map* comes with the maps (phase 5).
 */
const props = defineProps<{ type: ItemType; ids: readonly string[] }>()
const emit = defineEmits<{ done: [] }>()
const notify = useNotify()
const confirm = useConfirm()
const statuses = useStatuses()
const info = computed(() => itemTypes[props.type])

const open = ref<'person' | 'status' | 'color' | null>(null)
const personId = ref<string | null>(null)
const role = ref<string | null>(null)
const statusId = ref<string | null>(null)
const color = ref<string | null>(null)
const busy = ref(false)
const statusOptions = computed(() => selectableStatuses(statuses.all.value, props.type))
const what = computed(() =>
  props.ids.length === 1 ? `1 ${info.value.label}` : `${props.ids.length} ${info.value.plural.toLowerCase()}`,
)

watch(open, () => {
  personId.value = null
  role.value = null
  statusId.value = null
  color.value = null
})

function report(result: BulkResult, done: string) {
  if (result.changed)
    notify.success(
      `${done}: ${result.changed}`,
      result.skipped ? `${result.skipped} not changed (no access, or nothing to change).` : undefined,
    )
  else notify.info('Nothing changed', `${result.skipped} not changed (no access, or nothing to change).`)
}

async function run(action: () => Promise<BulkResult>, done: string) {
  busy.value = true
  try {
    report(await action(), done)
    open.value = null
    emit('done')
  } catch (e) {
    notify.error(e, 'Not changed')
  } finally {
    busy.value = false
  }
}

const apply = () => {
  if (open.value === 'person' && personId.value) {
    void run(() => bulkAssign(repo, props.ids, personId.value!, role.value), 'Assigned')
  } else if (open.value === 'status' && statusId.value) {
    void run(() => bulkPatch(repo, props.ids, { statusId: statusId.value }), 'Status changed')
  } else if (open.value === 'color') {
    void run(() => bulkPatch(repo, props.ids, { color: color.value }), 'Colour changed')
  }
}

function remove() {
  confirm.require({
    header: `Delete ${what.value}?`,
    message: 'They go to the Trash, from where they can be restored. Items you may not manage stay.',
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Delete', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: () => void run(() => bulkRemove(repo, props.ids), 'Deleted'),
  })
}

const headers = { person: 'Assign a person', status: 'Change the status', color: 'Change the colour' }
</script>

<template>
  <div class="bulk-actions" role="toolbar" :aria-label="`Actions on ${what}`" data-testid="bulk-actions">
    <span class="count">{{ what }} selected</span>
    <Button label="Assign person…" icon="pi pi-user-plus" size="small" text @click="open = 'person'" />
    <Button v-if="info.hasStatus" label="Status…" icon="pi pi-tag" size="small" text @click="open = 'status'" />
    <Button label="Colour…" icon="pi pi-palette" size="small" text @click="open = 'color'" />
    <Button label="Delete…" icon="pi pi-trash" size="small" text severity="danger" @click="remove" />
    <Button label="Clear selection" icon="pi pi-times" size="small" text severity="secondary" @click="emit('done')" />
  </div>
  <Dialog
    :visible="open !== null"
    :header="open ? `${headers[open]} – ${what}` : ''"
    modal
    :style="{ width: 'min(26rem, 100vw)' }"
    @update:visible="(v: boolean) => !v && (open = null)"
  >
    <div class="form">
      <template v-if="open === 'person'">
        <div class="field">
          <label for="bulk-person">Person</label>
          <PersonPicker v-model="personId" input-id="bulk-person" />
        </div>
        <div class="field">
          <label for="bulk-role">Role</label>
          <RoleInput v-model="role" input-id="bulk-role" />
        </div>
      </template>
      <div v-else-if="open === 'status'" class="field">
        <label for="bulk-status">Status</label>
        <Select
          v-model="statusId"
          input-id="bulk-status"
          :options="statusOptions"
          option-label="name"
          option-value="id"
          placeholder="Status"
        />
      </div>
      <ColorPicker v-else-if="open === 'color'" v-model="color" id-prefix="bulk-color" />
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="open = null" />
      <Button
        label="Apply"
        :loading="busy"
        :disabled="(open === 'person' && !personId) || (open === 'status' && !statusId)"
        @click="apply"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.bulk-actions {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  flex-wrap: wrap;
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  background: var(--p-highlight-background);
}

.count {
  font-weight: 600;
  margin-right: var(--pgpm-space-2);
}
</style>
