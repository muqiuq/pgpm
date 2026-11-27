<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import McpLogTab from './McpLogTab.vue'
import McpStatusTab from './McpStatusTab.vue'
import McpTokensTab from './McpTokensTab.vue'
import McpToolsTab from './McpToolsTab.vue'

/**
 * *Administration → MCP* (docs/03 §7.1, PNMTD's Admin → MCP), with *MCP: View*: the status, every user's tokens, the log of
 * every call, and the tools as the server's catalog has them. Switching a token off and *Revoke all* need *MCP: Manage*.
 * The tab is in the query (`?tab=log`), so a tab can be linked.
 */
const route = useRoute()
const keys = ['status', 'tokens', 'log', 'tools'] as const
type Tab = (typeof keys)[number]
const tab = computed<Tab>(() => {
  const t = route.query.tab
  return typeof t === 'string' && (keys as readonly string[]).includes(t) ? (t as Tab) : 'status'
})
const labels: Record<Tab, { label: string; icon: string }> = {
  status: { label: 'Status', icon: 'pi pi-chart-bar' },
  tokens: { label: 'Tokens', icon: 'pi pi-key' },
  log: { label: 'Log', icon: 'pi pi-list' },
  tools: { label: 'Tools', icon: 'pi pi-wrench' },
}
const tabs = keys.map((key) => ({ key, ...labels[key], to: { name: 'admin-mcp', query: { tab: key } } }))
</script>

<template>
  <div class="page">
    <PageHeader title="MCP" :crumbs="[{ label: 'Administration' }]" />
    <TabNav :items="tabs" :active="tab" label="MCP" />
    <McpStatusTab v-if="tab === 'status'" />
    <McpTokensTab v-else-if="tab === 'tokens'" />
    <McpLogTab v-else-if="tab === 'log'" />
    <McpToolsTab v-else />
  </div>
</template>
