<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { directoryApi, directoryKeys } from '@/api/directory'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'

/**
 * *Administration → Microsoft integration* (docs/03 §5, §8, docs/01 §16), with *Directory: Manage*: the Microsoft SSO's
 * settings as the server's configuration has them (read-only, never the secret), the directory sync's settings and its
 * last run – created, updated, deactivated, the error –, the persons by kind, and *Run now*: the background task wakes,
 * and this page follows it every 2 seconds until it is done.
 */
const notify = useNotify()
const queries = useQueryClient()
const status = useQuery({
  queryKey: directoryKeys.status,
  queryFn: directoryApi.status,
  refetchInterval: (query) =>
    query.state.data?.directory.running ||
    query.state.data?.directory.pending ||
    query.state.data?.lastRun?.status === 'running'
      ? 2000
      : 30_000,
})
const s = computed(() => status.data.value)
const busy = ref(false)
const refusal = ref<string | null>(null)
const working = computed(
  () => !!s.value && (s.value.directory.running || s.value.directory.pending || s.value.lastRun?.status === 'running'),
)
const canRun = computed(() => !!s.value?.directory.enabled && s.value.directory.configured)

async function run(full: boolean) {
  busy.value = true
  refusal.value = null
  try {
    await directoryApi.sync(full)
    notify.info(full ? 'Full run started' : 'Directory sync started', 'The page follows it until it is done.')
  } catch (e) {
    if (e instanceof ApiError && e.code === 'directory_off') {
      refusal.value = e.message
    } else {
      notify.error(e, 'Not started')
    }
  } finally {
    busy.value = false
    await queries.invalidateQueries({ queryKey: directoryKeys.status })
  }
}

const statusSeverity = (value: string | null | undefined) =>
  value === 'ok' ? 'success' : value === 'failed' ? 'danger' : value === 'running' ? 'info' : 'secondary'
const yesNo = (value: boolean) => (value ? 'yes' : 'no')
</script>

<template>
  <div class="page">
    <PageHeader title="Microsoft integration" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button
          label="Run now"
          icon="pi pi-sync"
          size="small"
          :loading="busy"
          :disabled="!canRun || working"
          @click="run(false)"
        />
        <Button
          label="Full run"
          icon="pi pi-refresh"
          size="small"
          severity="secondary"
          outlined
          :disabled="!canRun || busy || working"
          @click="run(true)"
        />
      </template>
    </PageHeader>
    <ErrorState v-if="status.isError.value" :error="status.error.value" what="the status" @retry="status.refetch()" />
    <div v-else-if="!s" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <Message v-if="refusal" severity="warn" :closable="false">{{ refusal }}</Message>
      <Message v-else-if="!s.directory.enabled" severity="info" :closable="false">
        The directory sync is off: <code>Directory__Enabled=true</code> switches it on (with the app registration of the
        SSO and the application permission <code>User.Read.All</code>).
      </Message>
      <Message v-else-if="!s.directory.configured" severity="warn" :closable="false">
        The directory sync needs <code>Entra__TenantId</code>, <code>Entra__ClientId</code> and
        <code>Entra__ClientSecret</code>.
      </Message>
      <div class="two-columns">
        <section class="panel" aria-labelledby="sso-title">
          <div id="sso-title" class="panel-header">
            Microsoft sign-in (SSO)
            <Tag
              class="right"
              :value="s.sso.enabled ? 'On' : 'Off'"
              :severity="s.sso.enabled ? 'success' : 'secondary'"
            />
          </div>
          <div class="panel-body">
            <dl class="facts">
              <dt>Tenant</dt>
              <dd class="mono">{{ s.sso.tenantId ?? '–' }}</dd>
              <dt>Client (application) id</dt>
              <dd class="mono">{{ s.sso.clientId ?? '–' }}</dd>
              <dt>Client secret</dt>
              <dd>{{ s.sso.clientSecretSet ? 'set' : 'not set' }}</dd>
              <dt>Redirect URI</dt>
              <dd>
                <CopyField v-if="s.sso.redirectUri" :value="s.sso.redirectUri" label="the redirect URI" />
                <span v-else class="muted">needs <code>PublicUrl</code></span>
              </dd>
              <dt>New users</dt>
              <dd>{{ s.sso.autoProvision ? 'created at their first sign-in' : 'only existing users sign in' }}</dd>
              <dt>Default group</dt>
              <dd>{{ s.sso.defaultGroup?.name ?? '–' }}</dd>
              <dt>App roles</dt>
              <dd>
                <template v-if="s.sso.appRoles.length">
                  <code v-for="role in s.sso.appRoles" :key="role" class="role">{{ role }}</code>
                </template>
                <span v-else class="muted">none (groups with an Entra app role)</span>
              </dd>
            </dl>
            <p class="muted small note">
              Set in the server's configuration (<code>Entra__*</code>, <code>.env</code>); one app registration serves
              the sign-in and the directory sync.
            </p>
          </div>
        </section>
        <section class="panel" aria-labelledby="directory-title">
          <div id="directory-title" class="panel-header">
            Directory sync
            <Tag
              class="right"
              :value="working ? 'Running' : s.directory.enabled ? 'On' : 'Off'"
              :severity="working ? 'info' : s.directory.enabled ? 'success' : 'secondary'"
            />
          </div>
          <div class="panel-body">
            <dl class="facts">
              <dt>Every</dt>
              <dd>{{ s.directory.intervalHours }} hours, in full every {{ s.directory.fullSyncDays }} days</dd>
              <dt>Configured</dt>
              <dd>{{ yesNo(s.directory.configured) }}</dd>
              <template v-if="s.directory.enabled && s.directory.configured">
                <dt>Next run</dt>
                <dd><RelativeTime :value="s.directory.nextRunAt" empty="–" /></dd>
                <dt>Next full run</dt>
                <dd><RelativeTime :value="s.directory.nextFullRunAt" empty="–" /></dd>
              </template>
              <template v-if="s.lastRun">
                <dt>Last run</dt>
                <dd>
                  <Tag :value="s.lastRun.status ?? '–'" :severity="statusSeverity(s.lastRun.status)" />
                  <span class="muted">
                    {{ s.lastRun.mode ?? '' }} · started {{ formatAbsolute(s.lastRun.startedAt) }}
                    <template v-if="s.lastRun.finishedAt && s.lastRun.status !== 'running'"
                      >· finished <RelativeTime :value="s.lastRun.finishedAt"
                    /></template>
                  </span>
                </dd>
                <dt>Result</dt>
                <dd>
                  {{ s.lastRun.created }} created · {{ s.lastRun.updated }} updated ·
                  {{ s.lastRun.deactivated }} deactivated · {{ s.lastRun.linked }} linked, of
                  {{ s.lastRun.seen }} accounts
                </dd>
                <template v-if="s.lastRun.error">
                  <dt>Error</dt>
                  <dd class="error">{{ s.lastRun.error }}</dd>
                </template>
                <dt>Last success</dt>
                <dd><RelativeTime :value="s.lastRun.lastSucceededAt" /></dd>
                <dt>Last full run</dt>
                <dd><RelativeTime :value="s.lastRun.lastFullAt" /></dd>
              </template>
              <template v-else>
                <dt>Last run</dt>
                <dd class="muted">none yet</dd>
              </template>
            </dl>
            <p class="muted small note">
              The whole tenant, members and guests. Disabled or removed accounts become inactive, never deleted; a full
              run also makes persons inactive that the directory no longer has.
            </p>
          </div>
        </section>
        <section class="panel" aria-labelledby="persons-title">
          <div id="persons-title" class="panel-header">Persons</div>
          <div class="panel-body">
            <dl class="facts">
              <dt>From Microsoft</dt>
              <dd>{{ s.persons.microsoft }} ({{ s.persons.active }} active, {{ s.persons.inactive }} inactive)</dd>
              <dt>Guests</dt>
              <dd>{{ s.persons.guests }}</dd>
              <dt>Linked to a user</dt>
              <dd>{{ s.persons.linked }}</dd>
              <dt>Manual</dt>
              <dd>{{ s.persons.manual }}</dd>
            </dl>
          </div>
        </section>
      </div>
    </template>
  </div>
</template>

<style scoped>
.right {
  margin-left: auto;
}

.role + .role {
  margin-left: var(--pgpm-space-2);
}

.note {
  margin: var(--pgpm-space-3) 0 0;
}

.small {
  font-size: 0.857rem;
}

.error {
  color: var(--pgpm-fail);
  overflow-wrap: anywhere;
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}
</style>
