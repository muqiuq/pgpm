<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { mcpApi, mcpKeys, type McpLogItem, type McpLogQuery, type McpTokenKind } from '@/api/mcp'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import McpLogEntry from './McpLogEntry.vue'
import { outcomeLabel, outcomeSeverity } from './mcpFormat'

/**
 * *MCP → Log* (PNMTD's log tab): every request, newest first – tool calls, tool lists and refused tokens. Filters: the
 * tool, the outcome, the token's kind, a time range, text in the summary or the user's name; one token from the Tokens
 * tab (`?token=`). A row opens what was asked, what came back, and what it changed.
 */
const route = useRoute()
const router = useRouter()
const text = (v: unknown) => (typeof v === 'string' && v ? v : null)
const tool = ref<string | null>(text(route.query.tool))
const outcome = ref<string | null>(text(route.query.outcome))
const kind = ref<McpTokenKind | null>(text(route.query.kind) as McpTokenKind | null)
const since = ref<string | null>(text(route.query.since))
const tokenId = computed(() => text(route.query.token))

const catalog = useQuery({ queryKey: mcpKeys.catalog, queryFn: mcpApi.catalog })
const tools = computed(() => (catalog.data.value?.tools ?? []).map((t) => t.name).sort())
const outcomes = ['ok', 'error', 'denied', 'rate_limited', 'unauthenticated'].map((o) => ({
  value: o,
  label: outcomeLabel(o),
}))
const kinds = [
  { value: 'Read', label: 'Read tokens' },
  { value: 'Write', label: 'Write tokens' },
]
const ranges = [
  { value: '1h', label: 'Last hour', ms: 3_600_000 },
  { value: '24h', label: 'Last 24 hours', ms: 86_400_000 },
  { value: '7d', label: 'Last 7 days', ms: 7 * 86_400_000 },
]

const filters = computed(() => {
  const range = ranges.find((r) => r.value === since.value)
  return {
    tool: tool.value ?? undefined,
    outcome: outcome.value ?? undefined,
    kind: kind.value ?? undefined,
    tokenId: tokenId.value ?? undefined,
    from: range ? new Date(Date.now() - range.ms).toISOString() : undefined,
  }
})
const urlQuery = computed(() => ({
  tab: 'log',
  tool: tool.value ?? undefined,
  outcome: outcome.value ?? undefined,
  kind: kind.value ?? undefined,
  since: since.value ?? undefined,
  token: tokenId.value ?? undefined,
}))
const active = computed(
  () => [tool.value, outcome.value, kind.value, since.value, tokenId.value].filter((v) => v).length,
)

const table = useServerTable<McpLogItem>({
  key: mcpKeys.log,
  fetch: (q) => mcpApi.log(q as McpLogQuery),
  filters,
  syncUrl: true,
  urlQuery,
})
const expanded = ref<Record<string, boolean>>({})
function toggle(e: McpLogItem) {
  expanded.value = expanded.value[e.id] ? {} : { [e.id]: true }
}

function clearToken() {
  void router.replace({ query: { ...route.query, token: undefined } })
}
</script>

<template>
  <FilterBar v-model:search="table.search.value" placeholder="Search summaries, users, tools" :active="active">
    <Select v-model="tool" :options="tools" placeholder="Any tool" show-clear filter size="small" aria-label="Tool" />
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
      v-model="kind"
      :options="kinds"
      option-label="label"
      option-value="value"
      placeholder="Both tokens"
      show-clear
      size="small"
      aria-label="Token kind"
    />
    <Select
      v-model="since"
      :options="ranges"
      option-label="label"
      option-value="value"
      placeholder="Any time"
      show-clear
      size="small"
      aria-label="Time"
    />
    <Button
      v-if="tokenId"
      label="One token"
      icon="pi pi-times"
      icon-pos="right"
      size="small"
      severity="secondary"
      outlined
      @click="clearToken"
    />
  </FilterBar>
  <ListTable v-model:expanded="expanded" :table="table" what="MCP requests" selectable fixed @select="toggle">
    <Column header="When" style="width: 8rem">
      <template #body="{ data }"><RelativeTime :value="data.at" /></template>
    </Column>
    <Column header="User" style="width: 11rem">
      <template #body="{ data }"
        ><span class="ellipsis"
          >{{ data.username || '–' }}
          <span class="muted">{{ data.kind === 'Write' ? 'write' : data.kind === 'Read' ? 'read' : '' }}</span></span
        ></template
      >
    </Column>
    <Column header="Tool" style="width: 11rem">
      <template #body="{ data }"
        ><code class="ellipsis">{{ data.tool ?? data.method }}</code></template
      >
    </Column>
    <Column header="Outcome" style="width: 9rem">
      <template #body="{ data }"
        ><Tag :value="outcomeLabel(data.outcome)" :severity="outcomeSeverity(data.outcome)" /><i
          v-if="data.strike"
          v-tooltip.top="'A strike'"
          role="img"
          class="pi pi-flag strike"
          aria-label="A strike"
      /></template>
    </Column>
    <Column header="Summary">
      <template #body="{ data }"
        ><span class="ellipsis" :title="data.summary">{{ data.summary }}</span></template
      >
    </Column>
    <Column header="Client" style="width: 10rem">
      <template #body="{ data }"
        ><span class="ellipsis muted" :title="data.client ?? ''">{{ data.client ?? '–' }}</span></template
      >
    </Column>
    <template #expansion="{ data }"><McpLogEntry :id="data.id" /></template>
    <template #mobile="{ item }">
      <div class="mobile-line">
        <code>{{ item.tool ?? item.method }}</code>
        <Tag :value="outcomeLabel(item.outcome)" :severity="outcomeSeverity(item.outcome)" />
        <RelativeTime :value="item.at" class="muted right" />
      </div>
      <div class="muted ellipsis">{{ item.username }} · {{ item.summary }}</div>
    </template>
  </ListTable>
</template>

<style scoped>
.strike {
  margin-left: var(--pgpm-space-1);
  color: var(--pgpm-fail);
}

.ellipsis {
  display: block;
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
