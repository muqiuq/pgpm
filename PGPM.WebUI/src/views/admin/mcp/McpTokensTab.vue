<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { mcpApi, mcpKeys, type McpAdminTokenItem } from '@/api/mcp'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'
import { endedText, tokenKindLabel, tokenPrefix, tokenSeverity, tokenStateText } from './mcpFormat'

/**
 * *MCP → Tokens* (PNMTD's tokens tab): every user's tokens, blocked first, then those on. Never a value, never switching
 * one on: an admin with *MCP: Manage* only switches a token off, with a reason its user reads.
 */
const session = useSession()
const notify = useNotify()
const queries = useQueryClient()
const tokens = useQuery({ queryKey: mcpKeys.tokens, queryFn: mcpApi.tokens, refetchInterval: 30_000 })
const canManage = computed(() => session.can('Mcp', 'Manage'))
const rows = computed(() => (tokens.data.value ?? []).filter((t) => t.token.hasValue || t.token.on || t.token.endedAt))
const target = ref<McpAdminTokenItem | null>(null)
const reason = ref('')
const busy = ref(false)

function ask(t: McpAdminTokenItem) {
  target.value = t
  reason.value = ''
}

async function turnOff() {
  if (!target.value) {
    return
  }
  busy.value = true
  try {
    await mcpApi.turnOffToken(target.value.id, reason.value.trim())
    notify.success('Token switched off')
    target.value = null
    await queries.invalidateQueries({ queryKey: mcpKeys.all })
  } catch (e) {
    notify.error(e, 'Not switched off')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState v-if="tokens.isError.value" :error="tokens.error.value" what="the tokens" @retry="tokens.refetch()" />
  <section v-else class="panel">
    <EmptyState v-if="tokens.data.value && !rows.length" icon="pi pi-key" title="Nobody has made a token yet." />
    <table v-else class="tokens">
      <thead>
        <tr>
          <th>User</th>
          <th>Token</th>
          <th>State</th>
          <th>Last used</th>
          <th>Strikes</th>
          <th />
        </tr>
      </thead>
      <tbody>
        <tr v-for="t in rows" :key="t.id">
          <td>
            {{ t.displayName }} <span class="muted">{{ t.username }}</span>
          </td>
          <td>
            {{ tokenKindLabel(t.token.kind) }}
            <code v-if="t.token.hint" class="muted">{{ tokenPrefix(t.token.kind) }}…{{ t.token.hint }}</code>
          </td>
          <td>
            <Tag :value="tokenStateText(t.token)" :severity="tokenSeverity(t.token)" />
            <span v-if="t.token.on && t.token.until" class="muted small">
              until <RelativeTime :value="t.token.until"
            /></span>
            <div v-if="!t.token.on && t.token.endedAt" class="muted small">
              {{ endedText(t.token.endedWhy) ?? 'Off' }} <RelativeTime :value="t.token.endedAt" />{{
                t.token.endedNote ? `: “${t.token.endedNote}”` : ''
              }}
            </div>
          </td>
          <td class="small">
            <template v-if="t.token.lastUsedAt"
              ><RelativeTime :value="t.token.lastUsedAt" /> · {{ t.token.lastClient ?? '' }} ·
              {{ t.token.lastUsedFrom ?? '' }}</template
            ><span v-else class="muted">never</span>
          </td>
          <td>{{ t.token.strikes || '' }}</td>
          <td class="actions">
            <RouterLink :to="{ name: 'admin-mcp', query: { tab: 'log', token: t.id } }">Log</RouterLink>
            <Button
              v-if="canManage && t.token.on"
              size="small"
              text
              severity="danger"
              label="Switch off…"
              @click="ask(t)"
            />
          </td>
        </tr>
      </tbody>
    </table>
  </section>
  <Dialog
    :visible="!!target"
    modal
    header="Switch this token off?"
    :style="{ width: 'min(28rem, 100vw)' }"
    @update:visible="(v: boolean) => !v && (target = null)"
  >
    <div class="form">
      <p class="text">
        {{ target?.displayName }}'s {{ target ? tokenKindLabel(target.token.kind).toLowerCase() : '' }} stops at once.
        They read your reason on Account → Security.
      </p>
      <div class="field">
        <label for="token-off-reason">Why</label>
        <InputText id="token-off-reason" v-model="reason" fluid maxlength="300" />
        <small class="field-hint">3 to 300 characters.</small>
      </div>
      <div class="form-actions end">
        <Button label="Cancel" text severity="secondary" @click="target = null" />
        <Button
          label="Switch off"
          severity="danger"
          :loading="busy"
          :disabled="reason.trim().length < 3"
          @click="turnOff"
        />
      </div>
    </div>
  </Dialog>
</template>

<style scoped>
.tokens {
  width: 100%;
  border-collapse: collapse;
}

.tokens th,
.tokens td {
  text-align: left;
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
  vertical-align: top;
}

.tokens th {
  color: var(--pgpm-muted);
  font-weight: 500;
}

.actions {
  white-space: nowrap;
  text-align: right;
}

.end {
  justify-content: flex-end;
}

.text {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
