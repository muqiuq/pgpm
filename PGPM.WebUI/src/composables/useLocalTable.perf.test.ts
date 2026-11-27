import { describe, expect, test } from 'vitest'
import type { ItemRow, StatusRow } from '@/local/rows'
import { emptyContext, itemText, matchItem, sortValue, type ItemFilters } from '@/views/items/itemList'
import { matchRows, normalise, type MatchSpec } from './useLocalTable'

/**
 * The item list with 10,000 ideas (docs/05 §3 phase 7, docs/07 §2): what the list computes on a keystroke of the search,
 * a filter or a sort – `matchRows` with the item list's own text, filter and sort value (views/items/itemList.ts).
 * `tools/npm.sh run bench` (`PGPM_PERF=1`; skipped by `npm test`): each case warmed up, then timed 20 times; the
 * median and the slowest are printed, and the median must stay below 60 ms (a regression guard: before docs/07's
 * changes the default sort took 232 ms).
 */
const N = 10_000
const words = [
  'website',
  'relaunch',
  'chatbot',
  'invoice',
  'Zürich',
  'forecast',
  'portal',
  'onboarding',
  'pricing',
  'archive',
]
const statuses: StatusRow[] = [
  'New',
  'Elaborating',
  'Assessed',
  'Ready for review',
  'Selected',
  'Watch',
  'Rejected',
].map(
  (name, i) =>
    ({
      id: `s${i}`,
      itemType: 'idea',
      name,
      color: '#64748b',
      icon: null,
      category: i < 4 ? 'open' : 'closed',
      sortOrder: (i + 1) * 10,
      isDefault: i === 0,
      maxDays: null,
      isArchived: false,
    }) as unknown as StatusRow,
)
const rows: ItemRow[] = Array.from({ length: N }, (_, i) => {
  const day = String(1 + (i % 28)).padStart(2, '0')
  return {
    id: `0193${i.toString(16).padStart(8, '0')}`,
    type: 'idea',
    number: i + 1,
    title: `${words[i % words.length]} ${words[(i * 7) % words.length]} idea ${i}`,
    description: i % 3 ? `Some text about ${words[(i * 3) % words.length]} and more, line ${i}.` : null,
    color: null,
    imageFileId: null,
    ownerUserId: `u${i % 25}`,
    statusId: `s${i % statuses.length}`,
    statusChangedAt: `2026-10-${day}T10:00:00.000000Z`,
    tags: [],
    originItemId: null,
    convertedToItemId: null,
    pinned: false,
    leadOrganisation: null,
    leadValue: null,
    leadCurrency: null,
    leadProbability: null,
    leadDecisionDate: null,
    leadNextAction: null,
    leadNextActionDue: null,
    leadSource: null,
    projectTemplateId: null,
    projectStartDate: null,
    projectEndDate: null,
    access: 'edit',
    seq: i + 1,
    rev: 1,
    deleted: false,
    deleteBatchId: null,
    createdAt: `2026-10-${day}T09:00:00.000000Z`,
    createdBy: null,
    updatedAt: `2026-11-${day}T${String(i % 24).padStart(2, '0')}:00:00.000000Z`,
    updatedBy: null,
  } as unknown as ItemRow
})
const ctx = emptyContext({ statuses: new Map(statuses.map((s) => [s.id, s])) })

function spec(search: string, keys: string[], filters: ItemFilters = {}): MatchSpec<ItemRow> {
  return {
    words: normalise(search.trim()).split(/\s+/).filter(Boolean),
    filters: filters as Record<string, unknown>,
    match: (item, f) => matchItem(item, f as ItemFilters, ctx),
    text: (item) => itemText(item, ctx),
    keys,
    sortValue: (item, field) => sortValue(item, field, ctx),
  }
}

const RUNS = 20

/** Median and maximum in ms of `RUNS` timed runs after three warm-up runs. */
function time(run: () => unknown): { median: number; max: number } {
  for (let i = 0; i < 3; i++) run()
  const times: number[] = []
  for (let i = 0; i < RUNS; i++) {
    const start = performance.now()
    run()
    times.push(performance.now() - start)
  }
  times.sort((a, b) => a - b)
  return { median: times[Math.floor(RUNS / 2)]!, max: times[RUNS - 1]! }
}

const cases: [string, MatchSpec<ItemRow>, number][] = [
  ['no search, newest change first (the default)', spec('', ['-updated_at']), 60],
  ['search one word', spec('chatbot', ['-updated_at']), 60],
  ['search two words, one with a diacritic', spec('zurich portal', ['-updated_at']), 60],
  ['sort by title', spec('', ['title']), 60],
  ['filter: two statuses, closed hidden', spec('', ['-updated_at'], { statusIds: ['s1', 's2'], hideClosed: true }), 60],
  ['sort by status, then newest change', spec('', ['status', '-updated_at']), 60],
]

describe.skipIf(!process.env.PGPM_PERF)(`item list, ${N.toLocaleString('en')} ideas`, () => {
  for (const [name, s, budget] of cases) {
    test(name, () => {
      const { median, max } = time(() => matchRows(rows, s))
      console.log(
        `${name}: median ${median.toFixed(1)} ms, max ${max.toFixed(1)} ms (${matchRows(rows, s).length} rows)`,
      )
      expect(median).toBeLessThan(budget)
    })
  }
})
