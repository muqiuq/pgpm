import { call, client, type Schemas } from './client'
import type { ListQuery } from '@/composables/useServerTable'

export type McpAccountItem = Schemas['McpAccountItem']
export type McpTokenItem = Schemas['McpTokenItem']
export type McpTokenIssued = Schemas['McpTokenIssued']
export type McpTokenKind = Schemas['McpTokenKind']
export type McpCatalog = Schemas['McpCatalog']
export type McpToolDoc = Schemas['McpToolDoc']
export type McpStatus = Schemas['McpStatus']
export type McpLogItem = Schemas['McpLogItem']
export type McpLogDetail = Schemas['McpLogDetail']
export type McpAdminTokenItem = Schemas['McpAdminTokenItem']
export type McpLogQuery = ListQuery & {
  userId?: string
  tokenId?: string
  kind?: McpTokenKind
  tool?: string
  outcome?: string
  from?: string
  to?: string
}

/** The query keys of the MCP pages (vue-query): one prefix, so a change refreshes them all. */
export const mcpKeys = {
  all: ['mcp'] as const,
  account: ['mcp', 'account'] as const,
  catalog: ['mcp', 'catalog'] as const,
  status: ['mcp', 'status'] as const,
  tokens: ['mcp', 'tokens'] as const,
  log: ['mcp', 'log'] as const,
  entry: (id: number) => ['mcp', 'entry', id] as const,
}

/** `read` or `write` in the account API's paths. */
const kindPath = (kind: McpTokenKind) => ({ params: { path: { kind: kind === 'Write' ? 'write' : 'read' } } })

/**
 * The MCP server's tokens and log (docs/03 §7.1): one's own two tokens (Account → Security), and Administration → MCP –
 * the catalog for everybody, status, log and tokens with *MCP: View*, switching off and revoking with *MCP: Manage*.
 */
export const mcpApi = {
  account: () => call(client.GET('/api/v1/account/mcp')),
  /** On, or the write token's hours extended; the value comes along when one was made now – shown once. */
  turnOn: (kind: McpTokenKind) => call(client.POST('/api/v1/account/mcp/{kind}/on', kindPath(kind))),
  turnOff: (kind: McpTokenKind) => call(client.POST('/api/v1/account/mcp/{kind}/off', kindPath(kind))),
  /** A new value, shown once; the old one stops working at once. */
  regenerate: (kind: McpTokenKind) => call(client.POST('/api/v1/account/mcp/{kind}/regenerate', kindPath(kind))),
  catalog: () => call(client.GET('/api/v1/mcp/catalog')),
  status: () => call(client.GET('/api/v1/mcp/status')),
  log: (query: McpLogQuery) => call(client.GET('/api/v1/mcp/log', { params: { query } })),
  entry: (id: number) => call(client.GET('/api/v1/mcp/log/{id}', { params: { path: { id } } })),
  tokens: () => call(client.GET('/api/v1/mcp/tokens')),
  turnOffToken: (id: string, reason: string) =>
    call(client.POST('/api/v1/mcp/tokens/{id}/off', { params: { path: { id } }, body: { reason } })),
  revokeAll: (reason: string) => call(client.POST('/api/v1/mcp/tokens/revoke-all', { body: { reason } })),
}
