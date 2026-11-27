<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { adminApi, adminKeys, type AuditEntryItem, type AuditQuery } from '@/api/admin'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'

/**
 * *Administration → Audit log* (PNMTD, docs/02 §12): security and administration events, newest first – sign-ins,
 * Microsoft provisioning and memberships, users, groups and rights, refusals. Filters: a family of events, the outcome,
 * the actor, a user (by or about them, from a user's drawer) and text. A row opens what changed, field by field.
 */
const route = useRoute()
const router = useRouter()

const families = [
  { value: 'auth', label: 'Sign-in' },
  { value: 'account', label: 'Own account' },
  { value: 'user', label: 'Users' },
  { value: 'group', label: 'Groups & rights' },
  { value: 'membership', label: 'Entra memberships' },
  { value: 'person', label: 'Persons' },
]
const outcomes = [
  { value: 'Succeeded', label: 'Succeeded' },
  { value: 'Failed', label: 'Failed' },
  { value: 'Denied', label: 'Denied' },
]
const actors = [
  { value: 'User', label: 'A user' },
  { value: 'Entra', label: 'Entra (Microsoft sign-in)' },
  { value: 'System', label: 'PGPM (CLI, seed)' },
  { value: 'Mcp', label: 'An MCP token' },
]
const text = (v: unknown) => (typeof v === 'string' && v ? v : null)
const prefix = ref<string | null>(text(route.query.prefix))
const outcome = ref<string | null>(text(route.query.outcome))
const actor = ref<string | null>(text(route.query.actor))
const userId = ref<string | null>(text(route.query.userId))
const userName = ref<string | null>(text(route.query.user))

const filters = computed(() => ({
  prefix: prefix.value ?? undefined,
  outcome: outcome.value ?? undefined,
  actor: actor.value ?? undefined,
  userId: userId.value ?? undefined,
}))
const urlQuery = computed(() => ({
  prefix: prefix.value ?? undefined,
  outcome: outcome.value ?? undefined,
  actor: actor.value ?? undefined,
  userId: userId.value ?? undefined,
  user: userId.value ? (userName.value ?? undefined) : undefined,
}))
const active = computed(() => [prefix.value, outcome.value, actor.value, userId.value].filter((v) => v).length)

const table = useServerTable<AuditEntryItem>({
  key: adminKeys.audit,
  fetch: (q) => adminApi.audit.list(q as AuditQuery),
  filters,
  syncUrl: true,
  urlQuery,
})

const tone = (e: AuditEntryItem) =>
  ({ Success: 'success', Warn: 'warn', Danger: 'danger', Neutral: 'secondary' })[e.tone] as
    'success' | 'warn' | 'danger' | 'secondary'
const who = (e: AuditEntryItem) =>
  e.actor === 'Entra'
    ? `Entra · ${e.actorName ?? ''}`
    : e.actor === 'System'
      ? (e.actorRef ?? 'PGPM')
      : (e.actorName ?? 'anonymous')

const expanded = ref<Record<string, boolean>>({})
function toggle(e: AuditEntryItem) {
  expanded.value = expanded.value[e.id] ? {} : { [e.id]: true }
}

function clearUser() {
  userId.value = null
  userName.value = null
  void router.replace({ query: { ...route.query, userId: undefined, user: undefined } })
}
</script>

<template>
  <div class="page">
    <PageHeader title="Audit log" :crumbs="[{ label: 'Administration' }]" />
    <FilterBar v-model:search="table.search.value" placeholder="Search names, summaries, addresses" :active="active">
      <Select
        v-model="prefix"
        :options="families"
        option-label="label"
        option-value="value"
        placeholder="All events"
        show-clear
        size="small"
        aria-label="Events"
      />
      <Select
        v-model="outcome"
        :options="outcomes"
        option-label="label"
        option-value="value"
        placeholder="Any outcome"
        show-clear
        size="small"
        aria-label="Outcome"
      />
      <Select
        v-model="actor"
        :options="actors"
        option-label="label"
        option-value="value"
        placeholder="Anyone"
        show-clear
        size="small"
        aria-label="Actor"
      />
      <Button
        v-if="userId"
        :label="`User: ${userName ?? userId}`"
        icon="pi pi-times"
        icon-pos="right"
        size="small"
        severity="secondary"
        outlined
        @click="clearUser"
      />
    </FilterBar>
    <ListTable v-model:expanded="expanded" :table="table" what="entries" selectable @select="toggle">
      <Column header="When" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Event" style="width: 15rem">
        <template #body="{ data }"><Tag :value="data.label" :severity="tone(data)" /></template>
      </Column>
      <Column header="Who" style="width: 12rem">
        <template #body="{ data }"
          ><span class="ellipsis">{{ who(data) }}</span></template
        >
      </Column>
      <Column header="Summary">
        <template #body="{ data }">
          <span class="ellipsis">{{ data.summary ?? data.subjectName ?? '' }}</span>
        </template>
      </Column>
      <Column header="Outcome" style="width: 8rem">
        <template #body="{ data }">
          <span :class="data.outcome === 'Succeeded' ? 'muted' : 'fail'"
            >{{ data.outcome.toLowerCase() }}{{ data.error ? ` (${data.error})` : '' }}</span
          >
        </template>
      </Column>
      <template #expansion="{ data }">
        <div class="detail">
          <dl class="facts">
            <dt>Time</dt>
            <dd>{{ formatAbsolute(data.at) }}</dd>
            <dt>Event</dt>
            <dd class="mono">{{ data.event }}</dd>
            <template v-if="data.subjectType">
              <dt>About</dt>
              <dd>{{ data.subjectType }} {{ data.subjectName ?? data.subjectId }}</dd>
            </template>
            <template v-if="data.request">
              <dt>Endpoint</dt>
              <dd class="mono">{{ data.request }}</dd>
            </template>
            <template v-if="data.ip">
              <dt>Address</dt>
              <dd class="mono">{{ data.ip }}</dd>
            </template>
            <template v-if="data.userAgent">
              <dt>Browser</dt>
              <dd class="ellipsis">{{ data.userAgent }}</dd>
            </template>
            <template v-if="data.correlationId">
              <dt>Request id</dt>
              <dd class="mono">{{ data.correlationId }}</dd>
            </template>
          </dl>
          <table v-if="data.changes?.length" class="changes">
            <thead>
              <tr>
                <th>Object</th>
                <th>Field</th>
                <th>Before</th>
                <th>After</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(c, i) in data.changes" :key="i">
                <td>{{ c.object }}</td>
                <td class="mono">{{ c.field }}</td>
                <template v-if="c.kind === 'secret'">
                  <td colspan="2" class="muted">changed (secret)</td>
                </template>
                <template v-else-if="c.kind === 'created' || c.kind === 'deleted'">
                  <td colspan="2" class="muted">{{ c.kind }}</td>
                </template>
                <template v-else-if="c.kind === 'text'">
                  <td colspan="2" class="muted">{{ c.summary }}</td>
                </template>
                <template v-else>
                  <td>{{ c.old ?? '–' }}</td>
                  <td>{{ c.new ?? '–' }}</td>
                </template>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
      <template #mobile="{ item }">
        <div class="mobile-line">
          <Tag :value="item.label" :severity="tone(item)" />
          <RelativeTime :value="item.at" class="muted right" />
        </div>
        <div class="muted ellipsis">{{ who(item) }} · {{ item.summary ?? '' }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.fail {
  color: var(--pgpm-fail);
}

.detail {
  display: grid;
  gap: var(--pgpm-space-3);
  padding: var(--pgpm-space-2) 0;
}

.changes {
  border-collapse: collapse;
  width: 100%;
  font-size: 0.857rem;
}

.changes th,
.changes td {
  text-align: left;
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-bottom: 1px solid var(--pgpm-border);
  overflow-wrap: anywhere;
}

.mobile-line {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.right {
  margin-left: auto;
}
</style>
