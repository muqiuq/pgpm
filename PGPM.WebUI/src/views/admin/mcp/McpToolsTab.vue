<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { mcpApi, mcpKeys, type McpToolDoc } from '@/api/mcp'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import { clientConfig, parameters } from './mcpFormat'

/**
 * *MCP → Tools* (PNMTD's documentation tab): how to connect, the rules with this installation's values, the most a token
 * gets per area, every tool with its kind, what it needs and its parameters, and what every client is told – all from
 * the server's own catalog, so it can't drift from the code.
 */
const catalog = useQuery({ queryKey: mcpKeys.catalog, queryFn: mcpApi.catalog })
const c = computed(() => catalog.data.value)
const search = ref('')
const kinds: Record<McpToolDoc['kind'], string> = {
  Read: 'reads',
  Write: 'writes',
  Action: 'acts',
  Delete: 'deletes',
  Ask: 'asks',
}
const tools = computed(() => {
  const q = search.value.trim().toLowerCase()
  return (c.value?.tools ?? []).filter(
    (t) => !q || `${t.name} ${t.title} ${t.description} ${t.right}`.toLowerCase().includes(q),
  )
})
const config = computed(() => (c.value ? clientConfig(c.value.url) : ''))
</script>

<template>
  <ErrorState v-if="catalog.isError.value" :error="catalog.error.value" what="the tools" @retry="catalog.refetch()" />
  <div v-else-if="!c" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <div v-else class="docs">
    <Message v-if="!c.enabled" severity="info" :closable="false">The MCP server is off (Mcp:Enabled).</Message>
    <section class="panel">
      <header class="panel-header">Connecting</header>
      <div class="body">
        <p>
          Each user switches their tokens on on <i>Account → Security</i>. A client sends one as
          <code>Authorization: Bearer pgpm_…</code> to this address (Streamable HTTP):
        </p>
        <CopyField :value="c.url" label="the MCP address" />
        <pre class="code">{{ config }}</pre>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">The rules</header>
      <div class="body">
        <ul>
          <li>
            The <b>read token</b> only reads, and stays on until {{ c.readDaysAfterOnline }} days after its user was
            last online in PGPM.
          </li>
          <li>
            The <b>write token</b> creates and edits, and stays on for {{ c.writeHours }} hours{{
              c.writeRequiresTotp ? '; it needs 2FA or a linked Microsoft account' : ''
            }}. It never deletes, shares existing items again or administers.
          </li>
          <li>
            A token sees what its user sees (sharing), with at most <i>read</i> or <i>edit</i> access, and has at most
            these rights:
          </li>
          <li>
            Limits: {{ c.callsPerMinute }} calls a minute per token, {{ c.writesPerMinute }} of them changes; results up
            to {{ c.maxResultChars.toLocaleString() }} characters.
          </li>
          <li>
            {{ c.strikesToBlock }} calls to tools the token isn't offered, within 24 hours, block it (an entry in the
            audit log). The log keeps the newest {{ c.logEntries.toLocaleString() }} calls.
          </li>
        </ul>
        <table class="ceiling">
          <thead>
            <tr>
              <th>Area</th>
              <th>Write token, at most</th>
              <th>Read token, at most</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in c.ceiling" :key="r.area">
              <td>{{ r.label }}</td>
              <td>{{ r.write ?? '–' }}</td>
              <td>{{ r.read ?? '–' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">
        Tools <span class="muted normal">({{ c.tools.length }})</span>
        <InputText
          v-model="search"
          placeholder="Search the tools"
          size="small"
          class="search"
          aria-label="Search the tools"
        />
      </header>
      <div class="body tools">
        <article v-for="t in tools" :key="t.name" class="tool">
          <div class="head">
            <code class="name">{{ t.name }}</code>
            <Tag
              :value="kinds[t.kind]"
              :severity="t.kind === 'Read' ? 'secondary' : t.kind === 'Delete' ? 'danger' : 'warn'"
            />
            <Tag v-if="!t.readToken" value="write token only" severity="contrast" />
            <Tag v-if="!t.available" value="not available now" severity="secondary" />
            <span class="muted small">Needs {{ t.right === 'none' ? 'no right' : t.right }}</span>
          </div>
          <p>{{ t.description }}</p>
          <ul v-if="parameters(t.parameters).length" class="params small">
            <li v-for="p in parameters(t.parameters)" :key="p.name">
              <code>{{ p.name }}</code> <span class="muted">{{ p.type }}{{ p.required ? ', required' : '' }}</span>
              {{ p.description }}
            </li>
          </ul>
        </article>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">What every client is told</header>
      <pre class="code instructions">{{ c.instructions }}</pre>
    </section>
  </div>
</template>

<style scoped>
.docs {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.body {
  padding: var(--pgpm-space-2) var(--pgpm-space-3) var(--pgpm-space-3);
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
  margin: var(--pgpm-space-2) 0 0;
}

.instructions {
  margin: var(--pgpm-space-3);
}

.ceiling {
  border-collapse: collapse;
  margin: var(--pgpm-space-2) 0;
}

.ceiling th,
.ceiling td {
  text-align: left;
  padding: 2px var(--pgpm-space-4) 2px 0;
}

.ceiling th {
  color: var(--pgpm-muted);
  font-weight: 500;
}

.search {
  margin-left: auto;
}

.tools {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.tool + .tool {
  border-top: 1px solid var(--pgpm-border);
  padding-top: var(--pgpm-space-2);
}

.head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.name {
  font-weight: 600;
}

.params {
  margin: 0;
  padding-left: var(--pgpm-space-4);
}

.normal {
  font-weight: 400;
}

.small {
  font-size: 0.857rem;
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

p {
  margin: var(--pgpm-space-1) 0;
}
</style>
