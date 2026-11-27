<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { rolesApi } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { localDb } from '@/local/db'
import { useOnline } from '@/local/lookups'
import type { AssignmentRoleRow } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * *Administration → Statuses → Assignment roles* (docs/03 §17, data model §7.10, *Configuration: Manage*): the
 * suggestions for an assignment's role (*Responsible*, *Review board*, …) in their order. New, edit (name, description,
 * order) and delete go through `/api/v1/config/roles` (online only); deleting one changes no assignment. The rows come
 * back through the sync.
 */
const notify = useNotify()
const online = useOnline()

const roles = useLive(
  async () =>
    ((await localDb().synced('assignment_roles').toArray()) as unknown as AssignmentRoleRow[])
      .filter((r) => !r.deleted)
      .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)),
  [] as AssignmentRoleRow[],
)

const tabs = [
  { key: 'statuses', label: 'Statuses', to: { name: 'statuses' } },
  { key: 'roles', label: 'Assignment roles', to: { name: 'roles' } },
]

const open = ref(false)
const edited = ref<AssignmentRoleRow | null>(null)
const name = ref('')
const description = ref('')
const sortOrder = ref<number | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
const title = computed(() => (edited.value ? `Edit role ${edited.value.name}` : 'New role'))

function openDialog(role: AssignmentRoleRow | null) {
  edited.value = role
  name.value = role?.name ?? ''
  description.value = role?.description ?? ''
  sortOrder.value =
    role?.sortOrder ?? (roles.value.value.length ? Math.max(...roles.value.value.map((r) => r.sortOrder)) + 10 : 10)
  errors.value = {}
  open.value = true
}

async function save() {
  const text = name.value.trim()
  errors.value = {
    name: !text ? 'A name is needed.' : text.length > 100 ? 'At most 100 characters.' : undefined,
    description: description.value.trim().length > 500 ? 'At most 500 characters.' : undefined,
  }
  if (!online.value || Object.values(errors.value).some(Boolean)) return
  saving.value = true
  const body = { name: text, description: description.value.trim() || null, sortOrder: sortOrder.value }
  try {
    if (edited.value) await rolesApi.update(edited.value.id, body)
    else await rolesApi.create(body)
    notify.success(edited.value ? 'Role saved' : 'Role created')
    open.value = false
    void engine.syncNow()
  } catch (e) {
    if (e instanceof ApiError && e.code === 'duplicate_name') {
      errors.value = { name: e.field('name') ?? 'This name is taken.' }
    } else if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), description: e.field('description'), sortOrder: e.field('sortOrder') }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}

const removing = ref<AssignmentRoleRow | null>(null)
const removeOpen = ref(false)
function askRemove(role: AssignmentRoleRow) {
  removing.value = role
  removeOpen.value = true
}

async function remove() {
  if (!removing.value) return
  saving.value = true
  try {
    await rolesApi.remove(removing.value.id)
    notify.success(`${removing.value.name} deleted`)
    removeOpen.value = false
    void engine.syncNow()
  } catch (e) {
    notify.error(e, 'Not deleted')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Assignment roles" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button label="New role" icon="pi pi-plus" size="small" :disabled="!online" @click="openDialog(null)" />
      </template>
    </PageHeader>
    <TabNav :items="tabs" active="roles" label="Statuses and roles" />
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: roles can be changed with a connection.
    </Message>
    <section class="panel" aria-label="Assignment roles">
      <EmptyState
        v-if="roles.loaded.value && !roles.value.value.length"
        icon="pi pi-id-card"
        title="No roles yet."
        text="Assignments then suggest the default roles (Responsible, Contact, Contributor, …)."
      />
      <ol v-else class="rows">
        <li v-for="r in roles.value.value" :key="r.id" :data-testid="`role-${r.id}`">
          <span class="name">
            <span class="strong ellipsis">{{ r.name }}</span>
            <small v-if="r.description" class="muted ellipsis">{{ r.description }}</small>
          </span>
          <small class="muted mono">{{ r.sortOrder }}</small>
          <span class="actions">
            <Button
              icon="pi pi-pencil"
              text
              size="small"
              severity="secondary"
              :disabled="!online"
              :aria-label="`Edit ${r.name}`"
              @click="openDialog(r)"
            />
            <Button
              icon="pi pi-trash"
              text
              size="small"
              severity="secondary"
              :disabled="!online"
              :aria-label="`Delete ${r.name}`"
              @click="askRemove(r)"
            />
          </span>
        </li>
      </ol>
    </section>
    <Dialog v-model:visible="open" :header="title" modal :style="{ width: 'min(30rem, 100vw)' }">
      <form id="role-form" class="form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="rl-name">Name</label>
          <InputText id="rl-name" v-model="name" maxlength="100" autofocus :invalid="!!errors.name" />
          <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
        </div>
        <div class="field">
          <label for="rl-description">Description</label>
          <Textarea id="rl-description" v-model="description" rows="2" auto-resize maxlength="500" />
          <small v-if="errors.description" class="field-error">{{ errors.description }}</small>
        </div>
        <div class="field">
          <label for="rl-order">Order</label>
          <InputNumber v-model="sortOrder" input-id="rl-order" :use-grouping="false" />
          <small v-if="errors.sortOrder" class="field-error">{{ errors.sortOrder }}</small>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="open = false" />
        <Button
          type="submit"
          form="role-form"
          :label="edited ? 'Save' : 'Create role'"
          :loading="saving"
          :disabled="!online"
        />
      </template>
    </Dialog>
    <Dialog
      v-model:visible="removeOpen"
      :header="removing ? `Delete ${removing.name}?` : 'Delete role'"
      modal
      :style="{ width: 'min(28rem, 100vw)' }"
    >
      <p>It is no longer suggested; assignments that have it keep their role.</p>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="removeOpen = false" />
        <Button label="Delete" severity="danger" :loading="saving" :disabled="!online" @click="remove" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 44px;
  padding: 0 var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
}

.rows li:last-child {
  border-bottom: none;
}

.name {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.actions {
  display: inline-flex;
}
</style>
