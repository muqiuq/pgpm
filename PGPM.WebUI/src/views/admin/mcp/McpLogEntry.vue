<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { mcpApi, mcpKeys } from '@/api/mcp'
import ErrorState from '@/components/ErrorState.vue'
import { formatAbsolute } from '@/format'
import { pretty } from './mcpFormat'

/**
 * One MCP log entry, loaded when its row opens: the arguments (redacted), the result, the error, and what the call
 * changed – the audit entries and History rows with the same request id.
 */
const props = defineProps<{ id: number }>()
const entry = useQuery({ queryKey: computed(() => mcpKeys.entry(props.id)), queryFn: () => mcpApi.entry(props.id) })
const e = computed(() => entry.data.value)
const summary = (changes: unknown) => {
  if (!Array.isArray(changes)) {
    return ''
  }
  return changes
    .map((c) => (typeof c === 'object' && c !== null && 'field' in c ? String((c as { field: unknown }).field) : ''))
    .filter((f) => f)
    .join(', ')
}
</script>

<template>
  <ErrorState v-if="entry.isError.value" :error="entry.error.value" what="the entry" @retry="entry.refetch()" />
  <div v-else-if="!e" class="loading"><ProgressSpinner style="width: 24px; height: 24px" /></div>
  <div v-else class="entry">
    <div v-if="e.error" class="block">
      <div class="muted small">Error</div>
      <pre class="code">{{ e.item.errorCode ?? '' }} {{ e.error }}</pre>
    </div>
    <div class="block">
      <div class="muted small">Arguments</div>
      <pre class="code">{{ pretty(e.arguments) }}</pre>
    </div>
    <div class="block">
      <div class="muted small">{{ e.truncated ? 'Result (shortened)' : 'Result' }}</div>
      <pre class="code">{{ pretty(e.result) }}</pre>
    </div>
    <div v-if="e.audit.length" class="block">
      <div class="muted small">Audit log</div>
      <ul class="list">
        <li v-for="a in e.audit" :key="a.id">
          <span class="mono">{{ a.event }}</span> · {{ a.summary ?? a.subjectName ?? '' }}
          <span class="muted">({{ a.outcome.toLowerCase() }}{{ a.actorRef ? `, ${a.actorRef}` : '' }})</span>
        </li>
      </ul>
    </div>
    <div v-if="e.history.length" class="block">
      <div class="muted small">History</div>
      <ul class="list">
        <li v-for="h in e.history" :key="h.id">
          <span class="mono">{{ h.entity }}</span> {{ h.action }}
          <span class="muted">{{ summary(h.changes) }} · {{ h.source }}</span>
        </li>
      </ul>
    </div>
    <div class="muted small">
      {{ formatAbsolute(e.item.at) }} · from {{ e.item.ip ?? 'unknown' }} · {{ e.userAgent ?? '' }} ·
      {{ e.item.durationMs }} ms · <span class="mono">{{ e.correlationId }}</span>
    </div>
  </div>
</template>

<style scoped>
.entry {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-2) 0;
}

.code {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--pgpm-mono);
  font-size: 0.8rem;
  margin: 0;
  max-height: 24rem;
  overflow: auto;
  background: var(--pgpm-bg);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pgpm-space-2);
}

.list {
  margin: 0;
  padding-left: var(--pgpm-space-4);
  font-size: 0.857rem;
}

.small {
  font-size: 0.857rem;
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-2);
}
</style>
