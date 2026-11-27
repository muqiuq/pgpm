<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import Drawer from 'primevue/drawer'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { adminApi, adminKeys, type UserItem } from '@/api/admin'
import { ApiError } from '@/api/client'
import CopyField from '@/components/CopyField.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import RightsTable from '@/components/RightsTable.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import UserMicrosoftAccount from './UserMicrosoftAccount.vue'

/**
 * Users (PNMTD, docs/01 §15.2): admin or their groups, how they sign in, 2FA, last sign-in, locked or not. A row opens
 * its drawer: edit (name, e-mail, admin, active, groups), the Microsoft account, their effective rights; the rare and
 * destructive actions – reset the password (a temporary one, shown once), reset 2FA, unlock, delete – in ⋯. The server
 * refuses to leave no active admin (409 last_admin) and to change memberships Entra manages, and says so.
 */
const session = useSession()
const queryClient = useQueryClient()
const { run, ask, notify, done } = useActions()
const groups = useQuery({ queryKey: adminKeys.groups, queryFn: adminApi.groups.list })
const groupOptions = computed(() =>
  (groups.data.value ?? []).map((g) => ({ value: g.id, label: g.name, disabled: !!g.entraAppRole })),
)
const access = (u: UserItem) => (u.isAdmin ? 'Admin' : u.groups.map((g) => g.name).join(', ') || 'no groups')

const table = useServerTable<UserItem>({
  key: adminKeys.users,
  fetch: (q) => adminApi.users.list(q),
  defaultSort: 'username',
  syncUrl: true,
})

const locked = (u: UserItem) => !!u.lockedUntil && Date.parse(u.lockedUntil) > Date.now()
const signIn = (u: UserItem) =>
  u.microsoftAccount ? (u.hasPassword ? 'Microsoft + password' : 'Microsoft') : 'Password'

// --- the drawer ---------------------------------------------------------------------------------
const selected = ref<UserItem | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const edit = ref({ displayName: '', email: '', isAdmin: false, isActive: true, groupIds: [] as string[] })
const editErrors = ref<Record<string, string | undefined>>({})
watch(selected, (u) => {
  if (u)
    edit.value = {
      displayName: u.displayName,
      email: u.email ?? '',
      isAdmin: u.isAdmin,
      isActive: u.isActive,
      groupIds: u.groups.map((g) => g.id),
    }
  editErrors.value = {}
})
const managedGroups = computed(() => selected.value?.groups.filter((g) => g.entra) ?? [])
const selectedAccess = useQuery({
  queryKey: computed(() => [...adminKeys.users, selected.value?.id, 'access']),
  queryFn: () => adminApi.users.access(selected.value!.id),
  enabled: computed(() => !!selected.value && !selected.value.isAdmin),
})
const effective = computed(() =>
  Object.fromEntries((selectedAccess.data.value?.rights ?? []).map((r) => [r.area, r.level])),
)
const sources = computed(() =>
  Object.fromEntries((selectedAccess.data.value?.rights ?? []).map((r) => [r.area, r.from])),
)
const isSelf = computed(() => selected.value?.id === session.user?.id)

async function reselect(id: string) {
  await queryClient.invalidateQueries({ queryKey: adminKeys.users })
  selected.value = await adminApi.users.get(id)
}

async function saveUser() {
  const u = selected.value!
  try {
    const saved = await adminApi.users.update(u.id, {
      displayName: edit.value.displayName,
      email: edit.value.email.trim() || null,
      isAdmin: edit.value.isAdmin,
      isActive: edit.value.isActive,
      groupIds: edit.value.groupIds,
    })
    const groupsChanged =
      [...edit.value.groupIds].sort().join() !==
      u.groups
        .map((g) => g.id)
        .sort()
        .join()
    notify.success(
      `${u.username} saved`,
      edit.value.isAdmin !== u.isAdmin || edit.value.isActive !== u.isActive
        ? 'Their sessions end.'
        : groupsChanged
          ? 'Their rights change from their next request.'
          : undefined,
    )
    selected.value = saved
    await queryClient.invalidateQueries({ queryKey: adminKeys.users })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      editErrors.value = { displayName: e.field('displayName'), email: e.field('email'), groupIds: e.field('groupIds') }
    } else {
      notify.error(e, 'Not saved')
    }
  }
}

// A temporary password is shown once, in a dialog of its own.
const shown = ref<{ username: string; password: string; expiresAt: string } | null>(null)
const shownOpen = computed({ get: () => shown.value !== null, set: (v) => !v && (shown.value = null) })

async function resetPassword() {
  const u = selected.value!
  if (
    !(await ask(
      `Reset the password of ${u.username}?`,
      'They get a temporary password (shown once, valid 7 days), their sessions end and a lockout is lifted.',
      'Reset password',
    ))
  )
    return
  const result = await run(() => adminApi.users.resetPassword(u.id), {
    failure: 'Not reset',
    invalidate: [adminKeys.users],
  })
  if (result) {
    shown.value = { username: u.username, password: result.password, expiresAt: result.expiresAt }
    await reselect(u.id)
  }
}

async function resetTotp() {
  const u = selected.value!
  if (
    !(await ask(
      `Turn off 2FA for ${u.username}?`,
      'For a lost authenticator: their sessions end, and the next sign-in offers the setup again.',
      'Turn off 2FA',
    ))
  )
    return
  if (
    await done(() => adminApi.users.resetTotp(u.id), {
      success: `2FA of ${u.username} turned off`,
      invalidate: [adminKeys.users],
    })
  )
    await reselect(u.id)
}

async function unlock() {
  const u = selected.value!
  if (
    await done(() => adminApi.users.unlock(u.id), { success: `${u.username} unlocked`, invalidate: [adminKeys.users] })
  )
    await reselect(u.id)
}

async function remove() {
  const u = selected.value!
  if (
    !(await ask(
      `Delete ${u.username}?`,
      'They can no longer sign in. Their person and their entries in the audit log stay.',
    ))
  )
    return
  if (
    await done(() => adminApi.users.remove(u.id), { success: `${u.username} deleted`, invalidate: [adminKeys.users] })
  )
    selected.value = null
}

const more = computed<MenuItem[]>(() => {
  const u = selected.value
  if (!u) return []
  return [
    ...(u.hasPassword || !u.microsoftAccount
      ? [{ label: 'Reset password…', icon: 'pi pi-key', disabled: isSelf.value, command: resetPassword }]
      : []),
    ...(u.isTotpEnabled
      ? [{ label: 'Turn off 2FA…', icon: 'pi pi-shield', disabled: isSelf.value, command: resetTotp }]
      : []),
    ...(locked(u) ? [{ label: 'Unlock', icon: 'pi pi-lock-open', command: unlock }] : []),
    { separator: true },
    { label: 'Delete…', icon: 'pi pi-trash', class: 'danger', disabled: isSelf.value, command: remove },
  ]
})

// --- a new user ---------------------------------------------------------------------------------
const creating = ref(false)
const draft = ref({ username: '', displayName: '', email: '', isAdmin: false, groupIds: [] as string[] })
const createErrors = ref<Record<string, string | undefined>>({})
async function create() {
  createErrors.value = { username: draft.value.username.trim() ? undefined : 'A username is needed.' }
  if (createErrors.value.username) return
  try {
    const result = await adminApi.users.create({
      username: draft.value.username.trim(),
      displayName: draft.value.displayName.trim() || null,
      email: draft.value.email.trim() || null,
      isAdmin: draft.value.isAdmin,
      groupIds: draft.value.groupIds,
    })
    creating.value = false
    draft.value = { username: '', displayName: '', email: '', isAdmin: false, groupIds: [] }
    shown.value = {
      username: result.user.username,
      password: result.temporaryPassword.password,
      expiresAt: result.temporaryPassword.expiresAt,
    }
    await queryClient.invalidateQueries({ queryKey: adminKeys.users })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      createErrors.value = {
        username: e.field('username') ?? (e.code === 'duplicate_key' ? e.message : undefined),
        displayName: e.field('displayName'),
        email: e.field('email'),
        groupIds: e.field('groupIds'),
      }
    } else {
      notify.error(e, 'Not created')
    }
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Users" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button label="New user" icon="pi pi-user-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search username, name or e-mail" />
    <ListTable
      :table="table"
      what="users"
      selectable
      :row-class="(u) => (u.isActive ? undefined : 'row-muted')"
      @select="(u) => (selected = u)"
    >
      <Column field="username" header="User" sortable>
        <template #body="{ data }">
          <span class="user">
            <span class="mono strong">{{ data.username }}</span>
            <span class="muted ellipsis">{{ data.displayName }}</span>
          </span>
        </template>
      </Column>
      <Column field="admin" header="Access" sortable style="width: 14rem">
        <template #body="{ data }">
          <Tag v-if="data.isAdmin" value="Admin" severity="info" />
          <span v-else-if="data.groups.length" class="ellipsis">{{ access(data) }}</span>
          <span v-else class="muted">no groups</span>
        </template>
      </Column>
      <Column header="Sign-in" style="width: 10rem">
        <template #body="{ data }">
          <span class="nowrap"><i v-if="data.microsoftAccount" class="pi pi-microsoft" /> {{ signIn(data) }}</span>
        </template>
      </Column>
      <Column header="2FA" style="width: 6rem">
        <template #body="{ data }">
          <span v-if="data.isTotpEnabled" class="on"><i class="pi pi-shield" /> on</span>
          <span v-else class="muted">off</span>
        </template>
      </Column>
      <Column header="State" style="width: 11rem">
        <template #body="{ data }">
          <Tag v-if="!data.isActive" value="inactive" severity="secondary" />
          <Tag v-else-if="locked(data)" value="locked" severity="danger" />
          <Tag v-else-if="data.mustChangePassword" value="password to change" severity="warn" />
          <span v-else class="muted">active</span>
        </template>
      </Column>
      <Column field="lastlogin" header="Last sign-in" sortable style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.lastLoginAt" /></template>
      </Column>
      <Column header="Last online" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="mobile-line">
          <span class="mono strong">{{ item.username }}</span>
          <Tag :value="access(item)" :severity="item.isAdmin ? 'info' : 'secondary'" />
          <i v-if="item.isTotpEnabled" class="pi pi-shield on" aria-label="2FA on" />
          <RelativeTime :value="item.lastLoginAt" class="muted right" />
        </div>
      </template>
    </ListTable>

    <Drawer v-model:visible="open" position="right" :style="{ width: 'min(32rem, 100vw)' }">
      <template #header>
        <div class="drawer-title">
          <span class="mono strong">{{ selected?.username }}</span>
          <MoreMenu v-if="selected" :items="more" label="More user actions" />
        </div>
      </template>
      <template v-if="selected">
        <dl class="facts">
          <dt>Created</dt>
          <dd>{{ formatAbsolute(selected.createdAt) }}</dd>
          <dt>Last sign-in</dt>
          <dd><RelativeTime :value="selected.lastLoginAt" /></dd>
          <dt>Last online</dt>
          <dd><RelativeTime :value="selected.lastSeenAt" /></dd>
          <dt>Password</dt>
          <dd>
            <template v-if="selected.hasPassword">
              changed <RelativeTime :value="selected.passwordChangedAt" />
              <template v-if="selected.temporaryPasswordExpiresAt">
                · temporary until {{ formatAbsolute(selected.temporaryPasswordExpiresAt) }}</template
              >
            </template>
            <span v-else class="muted">none – signs in with Microsoft</span>
          </dd>
          <dt>2FA</dt>
          <dd>{{ selected.isTotpEnabled ? 'on' : 'off' }}</dd>
          <dt>Failed sign-ins</dt>
          <dd>
            {{ selected.failedLoginCount }}
            <span v-if="locked(selected)" class="fail"> · locked until {{ formatAbsolute(selected.lockedUntil) }}</span>
          </dd>
        </dl>
        <form class="form section" novalidate @submit.prevent="saveUser">
          <div class="field">
            <label for="u-name">Name</label>
            <InputText id="u-name" v-model="edit.displayName" :invalid="!!editErrors.displayName" />
            <small v-if="editErrors.displayName" class="field-error">{{ editErrors.displayName }}</small>
          </div>
          <div class="field">
            <label for="u-email">E-mail</label>
            <InputText id="u-email" v-model="edit.email" type="email" :invalid="!!editErrors.email" />
            <small v-if="editErrors.email" class="field-error">{{ editErrors.email }}</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="edit.isAdmin" input-id="u-admin" />
            <label for="u-admin">Admin: every right, and users, groups and rights</label>
          </div>
          <div class="field">
            <label for="u-groups">Groups</label>
            <MultiSelect
              v-model="edit.groupIds"
              input-id="u-groups"
              :options="groupOptions"
              option-label="label"
              option-value="value"
              option-disabled="disabled"
              placeholder="No groups"
              display="chip"
              :invalid="!!editErrors.groupIds"
            />
            <small v-if="editErrors.groupIds" class="field-error">{{ editErrors.groupIds }}</small>
            <small v-else-if="managedGroups.length" class="field-hint"
              >{{ managedGroups.map((g) => g.name).join(', ') }}: from Entra app roles, set at each Microsoft
              sign-in.</small
            >
            <small v-else class="field-hint">Their rights are the highest level any of their groups gives.</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="edit.isActive" input-id="u-active" />
            <label for="u-active">Active (may sign in)</label>
          </div>
          <Message v-if="isSelf" severity="info" :closable="false"
            >This is you: your password and 2FA are under Account and Security.</Message
          >
          <div class="form-actions">
            <Button type="submit" label="Save" size="small" />
            <RouterLink :to="{ name: 'audit-log', query: { userId: selected.id, user: selected.username } }"
              >Audit log ›</RouterLink
            >
          </div>
        </form>
        <section class="section">
          <h3>Microsoft account</h3>
          <UserMicrosoftAccount :key="selected.id" :user="selected" @updated="(u) => reselect(u.id)" />
        </section>
        <section class="section">
          <h3>Rights</h3>
          <p v-if="selected.isAdmin" class="muted">Every right (admin).</p>
          <RightsTable
            v-else-if="selectedAccess.data.value"
            :model-value="effective"
            :catalog="session.catalogue"
            :sources="sources"
            readonly
          />
        </section>
      </template>
    </Drawer>

    <Dialog v-model:visible="creating" header="New user" modal :style="{ width: 'min(28rem, 100vw)' }">
      <form id="new-user" class="form" novalidate @submit.prevent="create">
        <div class="field">
          <label for="nu-username">Username</label>
          <InputText
            id="nu-username"
            v-model="draft.username"
            autofocus
            autocapitalize="none"
            :invalid="!!createErrors.username"
          />
          <small v-if="createErrors.username" class="field-error">{{ createErrors.username }}</small>
        </div>
        <div class="field">
          <label for="nu-name">Name</label>
          <InputText id="nu-name" v-model="draft.displayName" :invalid="!!createErrors.displayName" />
        </div>
        <div class="field">
          <label for="nu-email">E-mail</label>
          <InputText id="nu-email" v-model="draft.email" type="email" :invalid="!!createErrors.email" />
          <small v-if="createErrors.email" class="field-error">{{ createErrors.email }}</small>
        </div>
        <div class="switch-row">
          <ToggleSwitch v-model="draft.isAdmin" input-id="nu-admin" />
          <label for="nu-admin">Admin: every right, and users, groups and rights</label>
        </div>
        <div class="field">
          <label for="nu-groups">Groups</label>
          <MultiSelect
            v-model="draft.groupIds"
            input-id="nu-groups"
            :options="groupOptions"
            option-label="label"
            option-value="value"
            option-disabled="disabled"
            placeholder="No groups"
            display="chip"
            :invalid="!!createErrors.groupIds"
          />
          <small v-if="createErrors.groupIds" class="field-error">{{ createErrors.groupIds }}</small>
          <small v-else-if="!draft.isAdmin && !draft.groupIds.length" class="field-hint"
            >Without a group they see what is shared with them and create nothing.</small
          >
        </div>
        <small class="field-hint"
          >A local account: a temporary password is shown once, after creating; it must be changed at the first sign-in.
          Microsoft users need no account here – their first sign-in creates it.</small
        >
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="creating = false" />
        <Button type="submit" form="new-user" label="Create user" />
      </template>
    </Dialog>

    <Dialog v-model:visible="shownOpen" header="Temporary password" modal :style="{ width: 'min(28rem, 100vw)' }">
      <template v-if="shown">
        <p class="text">
          The temporary password of <strong>{{ shown.username }}</strong>, shown only now. It works until
          {{ formatAbsolute(shown.expiresAt) }} and must be changed at the first sign-in.
        </p>
        <CopyField :value="shown.password" label="Temporary password" />
      </template>
      <template #footer>
        <Button label="Done" @click="shown = null" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.user {
  display: inline-flex;
  gap: var(--pgpm-space-2);
  align-items: baseline;
  min-width: 0;
}

.strong {
  font-weight: 600;
}

.on {
  color: var(--pgpm-ok);
}

.fail {
  color: var(--pgpm-fail);
}

.drawer-title {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.section {
  margin-top: var(--pgpm-space-4);
  padding-top: var(--pgpm-space-4);
  border-top: 1px solid var(--pgpm-border);
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.section h3 {
  margin: 0;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.mobile-line {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.right {
  margin-left: auto;
}

.text {
  margin-top: 0;
}
</style>
