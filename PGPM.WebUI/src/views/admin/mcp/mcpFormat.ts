import type { McpTokenItem, McpTokenKind } from '@/api/mcp'

type Severity = 'success' | 'warn' | 'danger' | 'secondary' | 'info'

/** A token's state as people read it (docs/03 §7.1), on Account → Security and on Administration → MCP → Tokens. */
export function tokenStateText(t: Pick<McpTokenItem, 'state' | 'kind' | 'endedWhy'>): string {
  switch (t.state) {
    case 'on':
      return 'On'
    case 'blocked':
      return 'Blocked after refused calls'
    case 'expired':
      return t.kind === 'Write' ? 'Off: its hours are over' : 'Off: its user was not online for too long'
    case 'stamp':
      return 'Off: the password, 2FA or sessions changed'
    case 'totp':
      return 'Off: needs 2FA or a Microsoft account'
    case 'inactive':
      return 'Off: the user is deactivated'
    case 'no_right':
      return 'Refused: MCP: Use is missing'
    case 'mcp_off':
      return 'MCP is off'
    case 'no_value':
      return 'Off: no value yet'
    default:
      return endedText(t.endedWhy) ?? 'Off'
  }
}

/** Why a token ended (`ended_why`), for an off token's line; null when there is nothing to say. */
export function endedText(why: string | null | undefined): string | null {
  switch (why) {
    case 'off':
      return 'Switched off'
    case 'expired':
      return 'Off: its time was over'
    case 'regenerated':
      return 'Off: a new value was made'
    case 'stamp':
      return 'Off: the password, 2FA or sessions changed'
    case 'strikes':
      return 'Blocked after refused calls'
    case 'admin':
      return 'Switched off by an admin'
    case 'user_inactive':
      return 'Off: the user was deactivated'
    case 'totp_off':
      return 'Off: the second factor was turned off'
    default:
      return null
  }
}

export const tokenSeverity = (t: Pick<McpTokenItem, 'on' | 'state'>): Severity =>
  t.on ? 'success' : t.state === 'blocked' ? 'danger' : t.state === 'no_right' ? 'warn' : 'secondary'

export const tokenKindLabel = (kind: McpTokenKind) => (kind === 'Write' ? 'Write token' : 'Read token')

/** The prefix of a token's value: `pgpm_ro_` or `pgpm_rw_`. */
export const tokenPrefix = (kind: McpTokenKind) => (kind === 'Write' ? 'pgpm_rw_' : 'pgpm_ro_')

/** An MCP log entry's outcome as a tag. */
export const outcomeSeverity = (outcome: string): Severity =>
  outcome === 'ok'
    ? 'success'
    : outcome === 'error'
      ? 'warn'
      : outcome === 'rate_limited' || outcome === 'unauthenticated'
        ? 'secondary'
        : 'danger'

export const outcomeLabel = (outcome: string) => outcome.replace(/_/g, ' ')

/** JSON pretty, or the text as it is. */
export function pretty(text: string | null | undefined): string {
  if (!text) {
    return '–'
  }
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

/** A tool's parameters from its JSON Schema: name, type, required, description. */
export function parameters(schema: unknown): { name: string; type: string; required: boolean; description: string }[] {
  const s = (typeof schema === 'object' && schema !== null ? schema : {}) as {
    properties?: Record<string, { type?: string; description?: string; enum?: string[] }>
    required?: string[]
  }
  return Object.entries(s.properties ?? {}).map(([name, p]) => ({
    name,
    type: p.enum ? p.enum.join(' | ') : (p.type ?? ''),
    required: (s.required ?? []).includes(name),
    description: p.description ?? '',
  }))
}

/** An MCP client's configuration (Streamable HTTP) for PGPM's server, with the token or a placeholder. */
export function clientConfig(url: string, token?: string | null): string {
  return JSON.stringify(
    {
      mcpServers: {
        pgpm: { type: 'http', url, headers: { Authorization: `Bearer ${token ?? '<your token>'}` } },
      },
    },
    null,
    2,
  )
}
