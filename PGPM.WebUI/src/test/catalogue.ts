import type { RightsAreaItem } from '@/api/auth'

/** The server's rights catalogue (GET /api/v1/rights, docs/03 §6.2), as the tests' fixture. */
export const catalogue: RightsAreaItem[] = (
  [
    ['Notes', 'Notes', ['Create']],
    ['Ideas', 'Ideas', ['Create']],
    ['Leads', 'Leads', ['Create']],
    ['Projects', 'Projects', ['Create']],
    ['Maps', 'Maps', ['Create']],
    ['Persons', 'Persons', ['Edit']],
    ['Configuration', 'Configuration', ['Manage']],
    ['Directory', 'Directory', ['Manage']],
    ['Mcp', 'MCP', ['Use', 'View', 'Manage']],
    ['AuditLog', 'Audit log', ['View']],
    ['System', 'System', ['View']],
  ] as const
).map(([area, label, levels]) => ({
  area,
  label,
  levels: levels.map((level) => ({ level, allows: `${label} ${level}` })),
}))
