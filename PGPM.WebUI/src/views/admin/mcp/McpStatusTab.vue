<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref } from 'vue'
import { mcpApi, mcpKeys } from '@/api/mcp'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'
import { outcomeLabel } from './mcpFormat'

/**
 * *MCP → Status* (PNMTD's status tab): the tokens that are on, the calls of the last day and week, what they used and who,
 * and *Revoke all tokens* with *MCP: Manage*: every value stops working for good, and everyone makes a new one.
 */
const session = useSession()
const notify = useNotify()
const queries = useQueryClient()
const status = useQuery({ queryKey: mcpKeys.status, queryFn: mcpApi.status, refetchInterval: 30_000 })
const s = computed(() => status.data.value)
const canManage = computed(() => session.can('Mcp', 'Manage'))
const revoking = ref(false)
const reason = ref('')
const busy = ref(false)
const peak = computed(() => Math.max(1, ...(s.value?.days ?? []).map((d) => d.calls)))

async function revokeAll() {
  busy.value = true
  try {
    const result = await mcpApi.revokeAll(reason.value.trim())
    notify.success(
      'Tokens revoked',
      `${result.revoked} tokens no longer work. Everyone makes a new one on Account → Security.`,
    )
    revoking.value = false
    reason.value = ''
    await queries.invalidateQueries({ queryKey: mcpKeys.all })
  } catch (e) {
    notify.error(e, 'Not revoked')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState v-if="status.isError.value" :error="status.error.value" what="the status" @retry="status.refetch()" />
  <div v-else-if="!s" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <div v-else class="status">
    <Message v-if="!s.enabled" severity="info" :closable="false">The MCP server is off (Mcp:Enabled).</Message>
    <div class="tiles">
      <div class="tile panel">
        <div class="n">{{ s.readOn }}</div>
        <div class="muted small">read tokens on</div>
      </div>
      <div class="tile panel">
        <div class="n">{{ s.writeOn }}</div>
        <div class="muted small">write tokens on</div>
      </div>
      <div class="tile panel" :class="{ alert: s.blocked }">
        <div class="n">{{ s.blocked }}</div>
        <div class="muted small">blocked by strikes</div>
      </div>
      <div class="tile panel">
        <div class="n">{{ s.calls24h }}</div>
        <div class="muted small">calls in 24 hours</div>
      </div>
      <div class="tile panel">
        <div class="n">{{ s.calls7d }}</div>
        <div class="muted small">calls in 7 days</div>
      </div>
      <div class="tile panel" :class="{ alert: s.strikes24h }">
        <div class="n">{{ s.strikes24h }}</div>
        <div class="muted small">strikes in 24 hours</div>
      </div>
    </div>
    <p class="muted small">
      {{ s.withValue }} tokens have a value.
      <template v-if="s.lastCallAt">The last call: <RelativeTime :value="s.lastCallAt" />.</template>
    </p>

    <div class="columns">
      <section class="panel" aria-labelledby="days-title">
        <header id="days-title" class="panel-header">The last 7 days</header>
        <div class="body days">
          <div
            v-for="d in s.days"
            :key="d.date"
            class="day"
            :title="`${d.date}: ${d.calls} calls, ${d.failed} failed or refused`"
          >
            <div class="bar">
              <span class="ok" :style="{ height: `${((d.calls - d.failed) / peak) * 100}%` }" />
              <span class="failed" :style="{ height: `${(d.failed / peak) * 100}%` }" />
            </div>
            <div class="muted small">{{ d.date.slice(5) }}</div>
          </div>
        </div>
      </section>
      <section class="panel" aria-labelledby="outcomes-title">
        <header id="outcomes-title" class="panel-header">Outcomes, 24 hours</header>
        <ul class="body counts">
          <li v-for="o in s.outcomes24h" :key="o.name">
            <span>{{ outcomeLabel(o.name) }}</span><span class="mono">{{ o.count }}</span>
          </li>
          <li v-if="!s.outcomes24h.length" class="muted">No calls.</li>
        </ul>
      </section>
      <section class="panel" aria-labelledby="tools-title">
        <header id="tools-title" class="panel-header">Tools, 7 days</header>
        <ul class="body counts">
          <li v-for="t in s.tools7d" :key="t.name">
            <code>{{ t.name }}</code><span class="mono">{{ t.count }}</span>
          </li>
          <li v-if="!s.tools7d.length" class="muted">No calls.</li>
        </ul>
      </section>
      <section class="panel" aria-labelledby="users-title">
        <header id="users-title" class="panel-header">Users, 7 days</header>
        <ul class="body counts">
          <li v-for="u in s.users7d" :key="u.name">
            <span>{{ u.name }}</span><span class="mono">{{ u.count }}</span>
          </li>
          <li v-if="!s.users7d.length" class="muted">No calls.</li>
        </ul>
      </section>
    </div>

    <section v-if="canManage" class="panel danger-zone" aria-labelledby="revoke-title">
      <header id="revoke-title" class="panel-header">Revoke all tokens</header>
      <div class="body">
        <p>
          Every token's value stops working at once and for good, read and write alike. Each person makes a new one on
          Account → Security. For a leak, or when the rules change.
        </p>
        <Button label="Revoke all tokens…" severity="danger" icon="pi pi-ban" @click="revoking = true" />
      </div>
    </section>

    <Dialog v-model:visible="revoking" modal header="Revoke all tokens?" :style="{ width: 'min(30rem, 100vw)' }">
      <div class="form">
        <p class="text">
          {{ s.withValue }} tokens stop working at once. Everyone reads your reason on Account → Security.
        </p>
        <div class="field">
          <label for="revoke-reason">Why</label>
          <InputText id="revoke-reason" v-model="reason" fluid maxlength="300" />
          <small class="field-hint">3 to 300 characters.</small>
        </div>
        <div class="form-actions end">
          <Button label="Cancel" text severity="secondary" @click="revoking = false" />
          <Button
            label="Revoke all"
            severity="danger"
            :loading="busy"
            :disabled="reason.trim().length < 3"
            @click="revokeAll"
          />
        </div>
      </div>
    </Dialog>
  </div>
</template>

<style scoped>
.status {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
  gap: var(--pgpm-space-3);
}

.tile {
  padding: var(--pgpm-space-3);
}

.tile .n {
  font-size: 1.6rem;
  font-weight: 600;
}

.tile.alert .n {
  color: var(--pgpm-fail);
}

.columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
  gap: var(--pgpm-space-3);
  align-items: start;
}

.body {
  padding: var(--pgpm-space-2) var(--pgpm-space-3) var(--pgpm-space-3);
}

.days {
  display: flex;
  gap: var(--pgpm-space-2);
  align-items: flex-end;
  height: 9rem;
}

.day {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  height: 100%;
}

.bar {
  flex: 1;
  width: 100%;
  display: flex;
  flex-direction: column-reverse;
}

.bar .ok {
  background: color-mix(in srgb, var(--p-primary-color) 55%, transparent);
}

.bar .failed {
  background: var(--pgpm-fail);
}

.counts {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.counts li {
  display: flex;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
}

.danger-zone {
  border-color: var(--pgpm-fail);
}

.end {
  justify-content: flex-end;
}

.small {
  font-size: 0.857rem;
}

.text,
p {
  margin: 0 0 var(--pgpm-space-2);
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}
</style>
