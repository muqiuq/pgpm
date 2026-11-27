<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { systemApi, systemKeys, type SystemClientItem, type SystemTaskItem } from '@/api/system'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { agent, bytes, duration } from './systemFormat'

/**
 * *Administration → System* (docs/01 §3, docs/03 §9), with *System: View*: the instance (version, uptime), the database
 * and the files' disk, the sync (head, tombstone horizon, devices, live connections), the background tasks' health, the
 * directory sync and MCP at a glance, and every device with its cursor, what it has pending and whether it is live.
 * *Reset…* deletes a device's row: it pushes what it has, wipes its local data and pulls everything again.
 */
const notify = useNotify()
const queries = useQueryClient()
const status = useQuery({ queryKey: systemKeys.status, queryFn: systemApi.status, refetchInterval: 15_000 })
const s = computed(() => status.data.value)
const target = ref<SystemClientItem | null>(null)
/** The devices shown: the newest first, the first 25 until *Show all*; a search by user, browser or app version. */
const SHOWN = 25
const showAll = ref(false)
const query = ref('')
const matchingClients = computed(() => {
  const words = query.value.trim().toLowerCase().split(/\s+/).filter(Boolean)
  return (s.value?.clients ?? []).filter((c) => {
    const text = `${c.userName ?? ''} ${agent(c.userAgent)} ${c.appVersion ?? ''} ${c.live ? 'live' : ''}`.toLowerCase()
    return words.every((w) => text.includes(w))
  })
})
const shownClients = computed(() => (showAll.value ? matchingClients.value : matchingClients.value.slice(0, SHOWN)))
const busy = ref(false)

const lowDisk = computed(
  () => !!s.value && s.value.files.freeBytes !== null && s.value.files.freeBytes! < s.value.files.minFreeBytes * 2,
)
const staleTasks = computed(() => (s.value?.tasks ?? []).filter((t) => t.stale))

function taskTag(task: SystemTaskItem): { value: string; severity: 'success' | 'warn' | 'secondary' } {
  if (task.stale) return { value: 'Overdue', severity: 'warn' }
  if (!task.lastRunAt) return { value: 'Not run yet', severity: 'secondary' }
  return { value: 'OK', severity: 'success' }
}

async function reset() {
  if (!target.value) return
  busy.value = true
  try {
    await systemApi.resetClient(target.value.id)
    notify.success('Device reset', 'It loads everything again at its next sync.')
    target.value = null
    await queries.invalidateQueries({ queryKey: systemKeys.status })
  } catch (e) {
    notify.error(e, 'Not reset')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="System" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button
          label="Refresh"
          icon="pi pi-refresh"
          size="small"
          severity="secondary"
          outlined
          :loading="status.isFetching.value"
          @click="status.refetch()"
        />
      </template>
    </PageHeader>
    <ErrorState v-if="status.isError.value" :error="status.error.value" what="the status" @retry="status.refetch()" />
    <div v-else-if="!s" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <Message v-if="s.files.problem" severity="error" :closable="false">{{ s.files.problem }}</Message>
      <Message v-else-if="lowDisk" severity="warn" :closable="false">
        {{ bytes(s.files.freeBytes) }} free on the files' disk: uploads stop below {{ bytes(s.files.minFreeBytes) }}
        (<code>Files__MinFreeMb</code>).
      </Message>
      <Message v-if="staleTasks.length" severity="warn" :closable="false">
        Overdue: {{ staleTasks.map((t) => t.name).join(', ') }} – see the server's log.
      </Message>
      <div class="two-columns">
        <section class="panel" aria-labelledby="instance-title">
          <div id="instance-title" class="panel-header">Instance</div>
          <div class="panel-body">
            <dl class="facts">
              <dt>Version</dt>
              <dd>{{ s.instance.name }} {{ s.instance.version }}</dd>
              <dt>Running for</dt>
              <dd>
                {{ duration(s.instance.uptimeSeconds) }}
                <span class="muted">since {{ formatAbsolute(s.instance.startedAt) }}</span>
              </dd>
              <dt>Environment</dt>
              <dd>{{ s.instance.environment }}</dd>
              <dt>Public URL</dt>
              <dd>
                <code v-if="s.instance.publicUrl">{{ s.instance.publicUrl }}</code>
                <span v-else class="muted">not set (<code>PublicUrl</code>)</span>
              </dd>
              <dt>Background tasks</dt>
              <dd>{{ s.instance.backgroundTasks ? 'on' : 'off' }}</dd>
            </dl>
          </div>
        </section>
        <section class="panel" aria-labelledby="storage-title">
          <div id="storage-title" class="panel-header">Storage</div>
          <div class="panel-body">
            <dl class="facts">
              <dt>Database</dt>
              <dd>
                {{ bytes(s.database.sizeBytes) }} <span class="muted">PostgreSQL {{ s.database.serverVersion }}</span>
              </dd>
              <dt>Files</dt>
              <dd>{{ bytes(s.files.storedBytes) }} in {{ s.files.storedFiles }} files</dd>
              <dt>Free on the disk</dt>
              <dd :class="{ warn: lowDisk }">
                {{ bytes(s.files.freeBytes) }} <span class="muted">reserve {{ bytes(s.files.minFreeBytes) }}</span>
              </dd>
              <dt>Uploads in progress</dt>
              <dd>{{ s.files.pendingUploads }}</dd>
            </dl>
          </div>
        </section>
        <section class="panel" aria-labelledby="sync-title">
          <div id="sync-title" class="panel-header">
            Sync
            <Tag
              class="right"
              :value="s.sync.liveEnabled ? 'Live on' : 'Live off'"
              :severity="s.sync.liveEnabled ? 'success' : 'secondary'"
            />
          </div>
          <div class="panel-body">
            <dl class="facts">
              <dt>Head</dt>
              <dd class="mono">{{ s.sync.head }}</dd>
              <dt>Tombstone horizon</dt>
              <dd class="mono">{{ s.sync.tombstoneHorizon }}</dd>
              <dt>Devices</dt>
              <dd>{{ s.sync.clients }}</dd>
              <dt>Live connections</dt>
              <dd>
                {{ s.sync.liveConnections }}
                <span class="muted"
                  >of {{ s.sync.liveUsers }} users, at most {{ s.sync.maxConnectionsPerUser }} each</span
                >
              </dd>
              <dt>Pending changes</dt>
              <dd>{{ s.sync.pending }}</dd>
              <dt>Conflicts</dt>
              <dd>{{ s.sync.conflicts }}</dd>
            </dl>
            <p class="muted small note">As the devices last reported them.</p>
          </div>
        </section>
        <section class="panel" aria-labelledby="services-title">
          <div id="services-title" class="panel-header">Directory and MCP</div>
          <div class="panel-body">
            <dl class="facts">
              <dt><RouterLink :to="{ name: 'microsoft' }">Directory sync</RouterLink></dt>
              <dd>
                <template v-if="!s.directory.enabled">off</template>
                <template v-else-if="!s.directory.configured">not configured</template>
                <template v-else>
                  <Tag
                    :value="s.directory.lastStatus ?? 'not run yet'"
                    :severity="
                      s.directory.lastStatus === 'ok'
                        ? 'success'
                        : s.directory.lastStatus === 'failed'
                          ? 'danger'
                          : 'secondary'
                    "
                  />
                  <RelativeTime class="muted" :value="s.directory.lastFinishedAt" empty="" />
                </template>
              </dd>
              <template v-if="s.directory.lastError">
                <dt>Last error</dt>
                <dd class="error">{{ s.directory.lastError }}</dd>
              </template>
              <dt><RouterLink :to="{ name: 'admin-mcp' }">MCP</RouterLink></dt>
              <dd>
                {{ s.mcp.enabled ? 'on' : 'off' }}
                <span class="muted"
                  >· {{ s.mcp.tokensOn }} tokens on · {{ s.mcp.calls24h }} calls in 24 h,
                  {{ s.mcp.errors24h }} failed</span
                >
              </dd>
            </dl>
          </div>
        </section>
      </div>
      <section class="panel" aria-labelledby="tasks-title">
        <div id="tasks-title" class="panel-header">Background tasks</div>
        <div class="scroll">
          <table class="grid">
            <thead>
              <tr>
                <th>Task</th>
                <th>Every</th>
                <th>Last run</th>
                <th>Result</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr v-for="t in s.tasks" :key="t.key">
                <td>{{ t.name }}</td>
                <td class="muted">
                  {{ t.intervalMinutes ? duration(t.intervalMinutes * 60) : '–' }}
                </td>
                <td><RelativeTime :value="t.lastRunAt" empty="never" /></td>
                <td class="small">{{ t.summary ?? '' }}</td>
                <td class="end"><Tag v-bind="taskTag(t)" /></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section class="panel" aria-labelledby="clients-title">
        <div id="clients-title" class="panel-header">
          Devices <span class="muted small">{{ s.clients.length }}</span>
          <InputText
            v-if="s.clients.length > SHOWN"
            v-model="query"
            class="right search"
            size="small"
            placeholder="User, browser, version"
            aria-label="Search devices"
          />
        </div>
        <EmptyState v-if="!s.clients.length" icon="pi pi-desktop" title="No device has synced yet." />
        <div v-else class="scroll">
          <table class="grid">
            <thead>
              <tr>
                <th>User</th>
                <th>Device</th>
                <th>Last seen</th>
                <th>Behind</th>
                <th>Pending</th>
                <th>Conflicts</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr v-for="c in shownClients" :key="c.id">
                <td>{{ c.userName ?? c.userId }}</td>
                <td :title="c.userAgent ?? undefined">
                  {{ agent(c.userAgent) }} <span class="muted small">{{ c.appVersion ?? '' }}</span>
                </td>
                <td><RelativeTime :value="c.lastSeenAt" /></td>
                <td class="mono" :title="`Cursor ${c.cursor}`">{{ c.behind }}</td>
                <td>{{ c.pending || '' }}</td>
                <td>{{ c.conflicts || '' }}</td>
                <td class="end">
                  <Tag v-if="c.live" value="Live" severity="success" />
                  <Button size="small" text severity="danger" label="Reset…" @click="target = c" />
                </td>
              </tr>
            </tbody>
          </table>
          <div v-if="!showAll && matchingClients.length > SHOWN" class="more">
            <Button size="small" text :label="`Show all ${matchingClients.length}`" @click="showAll = true" />
          </div>
        </div>
      </section>
    </template>
  </div>
  <Dialog
    :visible="!!target"
    modal
    header="Reset this device?"
    :style="{ width: 'min(28rem, 100vw)' }"
    @update:visible="(v: boolean) => !v && (target = null)"
  >
    <div class="form">
      <p class="text">
        {{ target?.userName ?? 'The user' }}'s {{ agent(target?.userAgent) }} sends what it has, wipes its local data
        and loads everything again{{ target?.live ? ' – at once, it is live' : ' at its next sync' }}. Nothing on the
        server changes.
      </p>
      <div class="form-actions end">
        <Button label="Cancel" text severity="secondary" @click="target = null" />
        <Button label="Reset" severity="danger" :loading="busy" @click="reset" />
      </div>
    </div>
  </Dialog>
</template>

<style scoped>
.right {
  margin-left: auto;
}

.note {
  margin: var(--pgpm-space-3) 0 0;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pgpm-warn);
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

.scroll {
  overflow-x: auto;
}

.search {
  width: min(16rem, 50vw);
}

.more {
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
}

.grid {
  width: 100%;
  border-collapse: collapse;
}

.grid th,
.grid td {
  text-align: left;
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
  vertical-align: middle;
}

.grid th {
  color: var(--pgpm-muted);
  font-weight: 500;
  white-space: nowrap;
}

.end {
  text-align: right;
  white-space: nowrap;
  justify-content: flex-end;
}

.text {
  margin: 0;
}
</style>
