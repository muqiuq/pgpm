import { describe, expect, it } from 'vitest'
import type { McpTokenItem } from '@/api/mcp'
import {
  clientConfig,
  endedText,
  outcomeSeverity,
  parameters,
  pretty,
  tokenKindLabel,
  tokenPrefix,
  tokenSeverity,
  tokenStateText,
} from './mcpFormat'

const token = (patch: Partial<McpTokenItem>): McpTokenItem => ({
  kind: 'Read',
  on: false,
  state: 'off',
  until: null,
  hasValue: true,
  hint: null,
  issuedAt: null,
  lastUsedAt: null,
  lastUsedFrom: null,
  lastClient: null,
  endedAt: null,
  endedWhy: null,
  endedNote: null,
  blockedAt: null,
  strikes: 0,
  rights: {},
  access: 'read',
  ...patch,
})

describe('MCP tokens as people read them', () => {
  it('says why a token is off', () => {
    expect(tokenStateText(token({ state: 'on', on: true }))).toBe('On')
    expect(tokenStateText(token({ state: 'expired', kind: 'Write' }))).toBe('Off: its hours are over')
    expect(tokenStateText(token({ state: 'expired' }))).toBe('Off: its user was not online for too long')
    expect(tokenStateText(token({ endedWhy: 'admin' }))).toBe('Switched off by an admin')
    expect(tokenStateText(token({ state: 'blocked' }))).toBe('Blocked after refused calls')
    expect(tokenStateText(token({ state: 'totp', kind: 'Write' }))).toBe('Off: needs 2FA or a Microsoft account')
    expect(tokenStateText(token({ state: 'no_right' }))).toBe('Refused: MCP: Use is missing')
    expect(tokenStateText(token({}))).toBe('Off')
    expect(endedText('user_inactive')).toBe('Off: the user was deactivated')
    expect(endedText(null)).toBeNull()
  })

  it('colours the states and names the kinds', () => {
    expect(tokenSeverity(token({ state: 'blocked' }))).toBe('danger')
    expect(tokenSeverity(token({ on: true, state: 'on' }))).toBe('success')
    expect(tokenSeverity(token({ state: 'no_right' }))).toBe('warn')
    expect(tokenKindLabel('Write')).toBe('Write token')
    expect(tokenPrefix('Read')).toBe('pgpm_ro_')
    expect(outcomeSeverity('ok')).toBe('success')
    expect(outcomeSeverity('denied')).toBe('danger')
    expect(outcomeSeverity('rate_limited')).toBe('secondary')
  })
})

describe('the MCP pages’ helpers', () => {
  it('lists a tool’s parameters from its schema', () => {
    const schema = {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'The item' },
        type: { type: 'string', enum: ['note'], description: 'Only this type' },
      },
      required: ['id'],
    }
    expect(parameters(schema)).toEqual([
      { name: 'id', type: 'string', required: true, description: 'The item' },
      { name: 'type', type: 'note', required: false, description: 'Only this type' },
    ])
    expect(parameters(null)).toEqual([])
  })

  it('pretty-prints JSON and writes a client configuration', () => {
    expect(pretty('{"a":1}')).toBe('{\n  "a": 1\n}')
    expect(pretty('not json')).toBe('not json')
    expect(pretty(null)).toBe('–')
    const config = JSON.parse(clientConfig('https://pgpm.example/mcp', 'pgpm_ro_x'))
    expect(config.mcpServers.pgpm).toEqual({
      type: 'http',
      url: 'https://pgpm.example/mcp',
      headers: { Authorization: 'Bearer pgpm_ro_x' },
    })
    expect(clientConfig('u')).toContain('<your token>')
  })
})
