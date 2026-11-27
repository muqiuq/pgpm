<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { mcpApi, mcpKeys, type McpTokenItem, type McpTokenKind } from '@/api/mcp'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import {
  clientConfig,
  endedText,
  tokenKindLabel,
  tokenPrefix,
  tokenSeverity,
  tokenStateText,
} from '@/views/admin/mcp/mcpFormat'

/**
 * One's own MCP tokens (docs/03 §7.1, docs/01 §15.4, PNMTD's McpCard): a read token and a write token for an AI assistant
 * of one's own. The read token stays on until some days after one was last online here; the write token for some hours,
 * and needs 2FA or a linked Microsoft account. The value is shown once, when it is made; regenerating makes a new one.
 * Both act with the user's sharing and rights, capped: reading only, or creating and editing – never deleting, sharing
 * again or administering.
 */
const queries = useQueryClient()
const { ask, notify } = useActions()
const account = useQuery({ queryKey: mcpKeys.account, queryFn: mcpApi.account, refetchInterval: 30_000 })
const a = computed(() => account.data.value)
const busy = ref<McpTokenKind | null>(null)
const shown = ref<{ kind: McpTokenKind; value: string } | null>(null)

const tokens = computed(() => (a.value ? [a.value.read, a.value.write] : []))
const needsSecondFactor = (t: McpTokenItem) =>
  t.kind === 'Write' && !!a.value?.writeRequiresTotp && !a.value.hasSecondFactor
const canSwitchOn = (t: McpTokenItem) => !!a.value?.canUse && !needsSecondFactor(t)

/** A change of a token; a value that comes back (switched on the first time, regenerated) is shown once. */
async function act(kind: McpTokenKind, action: () => Promise<unknown>, done?: string) {
  busy.value = kind
  try {
    const result = await action()
    const value = result && typeof result === 'object' && 'value' in result ? result.value : null
    if (typeof value === 'string' && value) {
      shown.value = { kind, value }
    } else if (done) {
      notify.success(done)
    }
  } catch (e) {
    notify.error(e, 'Not done')
  } finally {
    busy.value = null
    await queries.invalidateQueries({ queryKey: mcpKeys.all })
  }
}

function toggle(t: McpTokenItem, on: boolean) {
  if (on) {
    void act(t.kind, () => mcpApi.turnOn(t.kind), `${tokenKindLabel(t.kind)} on`)
  } else {
    void act(t.kind, () => mcpApi.turnOff(t.kind), `${tokenKindLabel(t.kind)} off`)
  }
}

async function regenerate(t: McpTokenItem) {
  if (
    await ask(
      `A new value for the ${tokenKindLabel(t.kind).toLowerCase()}?`,
      'Assistants using the current value stop working at once. The new value is shown once.',
      'Regenerate',
    )
  ) {
    await act(t.kind, () => mcpApi.regenerate(t.kind))
  }
}

function lasts(t: McpTokenItem) {
  return t.kind === 'Write'
    ? `${a.value?.writeHours ?? 12} hours after switching it on`
    : `${a.value?.readDaysAfterOnline ?? 7} days after you were last online in PGPM`
}

const config = computed(() => (a.value ? clientConfig(a.value.url, shown.value?.value) : ''))
const claude = computed(() =>
  shown.value && a.value
    ? `claude mcp add --transport http pgpm ${a.value.url} --header "Authorization: Bearer ${shown.value.value}"`
    : '',
)
</script>

<template>
  <section class="panel" aria-labelledby="mcp-title">
    <div id="mcp-title" class="panel-header">MCP tokens <span class="muted normal">for AI assistants</span></div>
    <div class="panel-body body">
      <ErrorState
        v-if="account.isError.value"
        :error="account.error.value"
        what="your MCP tokens"
        @retry="account.refetch()"
      />
      <div v-else-if="!a" class="center"><ProgressSpinner style="width: 28px; height: 28px" /></div>
      <p v-else-if="!a.enabled" class="text muted">
        The MCP server is switched off on this installation (Mcp:Enabled).
      </p>
      <template v-else>
        <p class="text">
          An assistant (Claude, VS Code, Cursor …) works with your data through a token: it sees and changes what you
          may, capped. The read token only reads; the write token creates and edits, but never deletes, shares again or
          administers. Every call is logged, and changes appear in the History with the source MCP.
        </p>
        <Message v-if="!a.canUse" severity="warn" :closable="false">
          Tokens need the right <b>MCP: Use</b>. Ask an admin for a group that gives it.
        </Message>

        <div v-for="t in tokens" :key="t.kind" class="token" :data-testid="`mcp-token-${t.kind.toLowerCase()}`">
          <div class="head">
            <ToggleSwitch
              :model-value="t.on"
              :input-id="`mcp-${t.kind}`"
              :disabled="busy !== null || (!t.on && !canSwitchOn(t))"
              @update:model-value="(on: boolean) => toggle(t, on)"
            />
            <label :for="`mcp-${t.kind}`" class="strong">{{ tokenKindLabel(t.kind) }}</label>
            <Tag :value="tokenStateText(t)" :severity="tokenSeverity(t)" />
            <code v-if="t.hint" class="muted small">{{ tokenPrefix(t.kind) }}…{{ t.hint }}</code>
          </div>
          <p class="small muted">
            <template v-if="t.on && t.until"
              >On until {{ formatAbsolute(t.until) }} (<RelativeTime :value="t.until" />).
            </template>
            {{ t.kind === 'Write' ? 'Creates and edits' : 'Reads' }}; lasts {{ lasts(t) }}.
          </p>
          <p v-if="!t.on && t.endedAt" class="small muted">
            {{ endedText(t.endedWhy) ?? 'Off' }} <RelativeTime :value="t.endedAt" />{{
              t.endedNote ? `: “${t.endedNote}”` : '.'
            }}
          </p>
          <p v-if="needsSecondFactor(t)" class="small muted">
            The write token needs two-factor authentication or a linked Microsoft account:
            <RouterLink :to="{ name: 'setup-2fa', query: { returnUrl: '/account/security' } }">set up 2FA</RouterLink>.
          </p>
          <p v-if="t.hasValue" class="small muted">
            Last used <RelativeTime :value="t.lastUsedAt" />
            <template v-if="t.lastUsedAt">· {{ t.lastClient ?? 'a client' }} · {{ t.lastUsedFrom ?? '' }}</template>
            <template v-if="t.strikes"> · {{ t.strikes }} refused calls in 24 hours</template>
          </p>
          <div class="actions">
            <Button
              v-if="t.on && t.kind === 'Write'"
              size="small"
              severity="secondary"
              outlined
              :label="`Extend to ${a.writeHours} hours`"
              :loading="busy === t.kind"
              @click="act(t.kind, () => mcpApi.turnOn(t.kind), `On for ${a.writeHours} more hours`)"
            />
            <Button
              v-if="t.hasValue"
              size="small"
              severity="secondary"
              text
              label="Regenerate…"
              icon="pi pi-refresh"
              :disabled="busy !== null || !a.canUse"
              @click="regenerate(t)"
            />
          </div>
        </div>

        <details class="connect">
          <summary>How to connect an assistant</summary>
          <p class="small">
            Your assistant sends the token as <code>Authorization: Bearer pgpm_…</code> to this address:
          </p>
          <CopyField :value="a.url" label="the MCP address" />
          <p class="small muted">
            Streamable HTTP. Most clients take a configuration like this (Claude Desktop, Cursor, VS Code under
            <code>servers</code>):
          </p>
          <pre class="code">{{ config }}</pre>
        </details>
      </template>
    </div>

    <Dialog
      :visible="!!shown"
      modal
      header="Your new token"
      :style="{ width: 'min(40rem, 100vw)' }"
      @update:visible="(v: boolean) => !v && (shown = null)"
    >
      <div v-if="shown && a" class="form">
        <Message severity="warn" :closable="false">
          This is the only time PGPM shows this value. Store it in your assistant now; if it is lost, regenerate it.
        </Message>
        <CopyField :value="shown.value" label="the token" />
        <p class="small muted">Claude Code:</p>
        <CopyField :value="claude" label="the command" />
        <p class="small muted">Other clients:</p>
        <pre class="code">{{ config }}</pre>
        <div class="form-actions end"><Button label="I've stored it" @click="shown = null" /></div>
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.text {
  margin: 0;
}

.normal {
  font-weight: 400;
}

.strong {
  font-weight: 600;
}

.token {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  border-top: 1px solid var(--pgpm-border);
  padding-top: var(--pgpm-space-2);
}

.head,
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.token p {
  margin: 0;
}

.small {
  font-size: 0.9286rem;
}

.connect {
  border-top: 1px solid var(--pgpm-border);
  padding-top: var(--pgpm-space-2);
}

.connect summary {
  cursor: pointer;
  font-weight: 500;
}

.connect > * + * {
  margin-top: var(--pgpm-space-2);
}

.code {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--pgpm-mono);
  font-size: 0.857rem;
  background: var(--pgpm-bg);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pgpm-space-2);
  margin: 0;
}

.center {
  display: flex;
  justify-content: center;
}

.end {
  justify-content: flex-end;
}
</style>
