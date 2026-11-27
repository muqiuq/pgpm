<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { adminApi, adminKeys, type Rights } from '@/api/admin'
import { ApiError } from '@/api/client'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RightsTable from '@/components/RightsTable.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'

/**
 * A group's page (PNMTD): its name, description and Entra app role, its rights as one table (a level per area from the
 * server's catalogue), and its members. Saving applies to the members from their next request; nobody is signed out.
 * With an Entra app role its members follow the role at every Microsoft sign-in and can't be edited here.
 */
const props = defineProps<{ id: string }>()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run, done, ask, notify } = useActions()

const group = useQuery({
  queryKey: computed(() => [...adminKeys.groups, props.id]),
  queryFn: () => adminApi.groups.get(props.id),
})
const users = useQuery({ queryKey: [...adminKeys.users, 'all'], queryFn: () => adminApi.users.list({ pageSize: 500 }) })

const form = ref<{ name: string; description: string; entraAppRole: string; rights: Rights }>({
  name: '',
  description: '',
  entraAppRole: '',
  rights: {},
})
const errors = ref<Record<string, string | undefined>>({})
watch(
  () => group.data.value,
  (g) => {
    if (g)
      form.value = {
        name: g.name,
        description: g.description ?? '',
        entraAppRole: g.entraAppRole ?? '',
        rights: { ...g.rights },
      }
  },
  { immediate: true },
)
const sorted = (r: Rights) => JSON.stringify(Object.entries(r).sort())
const dirty = computed(() => {
  const g = group.data.value
  return (
    !!g &&
    (g.name !== form.value.name ||
      (g.description ?? '') !== form.value.description ||
      (g.entraAppRole ?? '') !== form.value.entraAppRole.trim().toUpperCase() ||
      sorted(g.rights) !== sorted(form.value.rights))
  )
})
const managed = computed(() => !!group.data.value?.entraAppRole)

async function save() {
  errors.value = {}
  try {
    await adminApi.groups.update(props.id, {
      name: form.value.name.trim(),
      description: form.value.description.trim() || null,
      entraAppRole: form.value.entraAppRole.trim() || null,
      rights: form.value.rights,
    })
    notify.success(`${form.value.name} saved`, 'Its members have the new rights from their next request.')
    await queryClient.invalidateQueries({ queryKey: adminKeys.groups })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      errors.value = {
        name:
          e.field('name') ?? (e.code === 'duplicate_key' && !e.message.includes('app role') ? e.message : undefined),
        entraAppRole:
          e.field('entraAppRole') ??
          (e.code === 'duplicate_key' && e.message.includes('app role') ? e.message : undefined),
        rights: e.field('rights'),
      }
    } else {
      notify.error(e, 'Not saved')
    }
  }
}

const memberIds = computed(() => (group.data.value?.members ?? []).map((m) => m.id))
const candidates = computed(() =>
  (users.data.value?.items ?? [])
    .filter((u) => !memberIds.value.includes(u.id))
    .map((u) => ({ id: u.id, label: `${u.username} (${u.displayName})` })),
)
const adding = ref<string | null>(null)
const setMembers = (ids: string[], success: string) =>
  run(() => adminApi.groups.members(props.id, ids), {
    success,
    failure: 'Members not saved',
    invalidate: [adminKeys.groups, adminKeys.users],
  })
async function add() {
  if (!adding.value) return
  const name = candidates.value.find((c) => c.id === adding.value)?.label ?? 'Member'
  if (await setMembers([...memberIds.value, adding.value], `${name} added`)) adding.value = null
}
const removeMember = (id: string, name: string) =>
  setMembers(
    memberIds.value.filter((m) => m !== id),
    `${name} removed`,
  )

async function remove() {
  const g = group.data.value!
  const members =
    g.members.length === 0
      ? 'It has no members.'
      : `Its ${g.members.length} members lose what it gave them: ${g.members.map((m) => m.username).join(', ')}.`
  if (
    !(await ask(
      `Delete ${g.name}?`,
      `${members} Their accounts stay; items shared with it are no longer shared with them.`,
      'Delete group',
    ))
  )
    return
  if (
    await done(() => adminApi.groups.remove(props.id), {
      success: `${g.name} deleted`,
      invalidate: [adminKeys.groups, adminKeys.users],
    })
  ) {
    await router.push({ name: 'groups' })
  }
}

const more = computed<MenuItem[]>(() => [{ label: 'Delete group…', icon: 'pi pi-trash', command: remove }])
</script>

<template>
  <div class="page">
    <ErrorState v-if="group.isError.value" :error="group.error.value" what="the group" @retry="group.refetch()" />
    <div v-else-if="!group.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <PageHeader
        :title="group.data.value.name"
        :crumbs="[{ label: 'Administration' }, { label: 'Groups & Rights', to: { name: 'groups' } }]"
      >
        <template #actions>
          <MoreMenu :items="more" label="More group actions" />
          <Button label="Save" icon="pi pi-check" size="small" :disabled="!dirty" @click="save" />
        </template>
      </PageHeader>
      <div class="columns">
        <section class="panel" aria-labelledby="rights-title">
          <div id="rights-title" class="panel-header">Rights</div>
          <div class="panel-body">
            <small v-if="errors.rights" class="field-error">{{ errors.rights }}</small>
            <RightsTable v-model="form.rights" :catalog="session.catalogue" />
          </div>
        </section>
        <div class="side">
          <section class="panel" aria-labelledby="group-title">
            <div id="group-title" class="panel-header">Group</div>
            <form class="panel-body form" novalidate @submit.prevent="save">
              <div class="field">
                <label for="g-name">Name</label>
                <InputText id="g-name" v-model="form.name" :invalid="!!errors.name" />
                <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
              </div>
              <div class="field">
                <label for="g-description">Description</label>
                <InputText id="g-description" v-model="form.description" />
              </div>
              <div class="field">
                <label for="g-role">Entra app role</label>
                <InputText
                  id="g-role"
                  v-model="form.entraAppRole"
                  class="mono"
                  placeholder="none"
                  :invalid="!!errors.entraAppRole"
                />
                <small v-if="errors.entraAppRole" class="field-error">{{ errors.entraAppRole }}</small>
                <small v-else class="field-hint"
                  >With an app role's value (e.g. PGPM.MEMBERS), the members are whoever holds the role at their
                  Microsoft sign-in.</small
                >
              </div>
            </form>
          </section>
          <section class="panel" aria-labelledby="members-title">
            <div id="members-title" class="panel-header">Members ({{ group.data.value.members.length }})</div>
            <div class="panel-body">
              <Message v-if="managed" severity="info" :closable="false"
                >Set by the Entra app role {{ group.data.value.entraAppRole }} at each Microsoft sign-in.</Message
              >
              <ul class="members">
                <li v-for="m in group.data.value.members" :key="m.id" :class="{ 'row-muted': !m.isActive }">
                  <span
                    ><span class="mono">{{ m.username }}</span> <span class="muted">{{ m.displayName }}</span></span
                  >
                  <Button
                    v-if="!managed"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Remove ${m.username}`"
                    @click="removeMember(m.id, m.username)"
                  />
                </li>
                <li v-if="!group.data.value.members.length" class="muted">No members yet.</li>
              </ul>
              <div v-if="!managed" class="add">
                <Select
                  v-model="adding"
                  :options="candidates"
                  option-label="label"
                  option-value="id"
                  placeholder="Add a user…"
                  filter
                  size="small"
                  aria-label="User to add"
                />
                <Button label="Add" size="small" :disabled="!adding" @click="add" />
              </div>
            </div>
          </section>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.columns {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(16rem, 1fr);
  gap: var(--pgpm-space-4);
  align-items: start;
}

.side {
  display: grid;
  gap: var(--pgpm-space-4);
}

.members {
  list-style: none;
  margin: var(--pgpm-space-2) 0 var(--pgpm-space-3);
  padding: 0;
}

.members li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--pgpm-space-1) 0;
}

.add {
  display: flex;
  gap: var(--pgpm-space-2);
}

.add :deep(.p-select) {
  flex: 1;
  min-width: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

@media (max-width: 899px) {
  .columns {
    grid-template-columns: 1fr;
  }
}
</style>
