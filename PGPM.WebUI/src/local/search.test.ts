import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { PgpmDb } from './db'
import { itemRank, personRank, searchLocal } from './search'

describe('the global search (docs/01 §3): items and persons, locally', () => {
  let db: PgpmDb

  beforeEach(async () => {
    db = memoryDb()
    const item = (
      id: string,
      type: string,
      number: number | null,
      title: string,
      description: string | null = null,
    ) => ({
      id,
      type,
      number,
      title,
      description,
      deleted: false,
      updatedAt: `2026-11-0${id.slice(-1)}T00:00:00Z`,
    })
    await db
      .synced('items')
      .bulkPut([
        item('i1', 'idea', 42, 'Website chatbot'),
        item('p2', 'project', 7, 'Relaunch of the website'),
        item('n3', 'note', 3, 'Workshop', 'Ideas for the Website in Zürich'),
        item('l4', 'lead', 5, 'Website for Müller AG'),
        { ...item('n5', 'note', 9, 'Website gone'), deleted: true },
      ])
    await db.synced('persons').bulkPut([
      { id: 'pa', displayName: 'Ana Muster', email: 'ana@example.org', isActive: true, deleted: false },
      { id: 'pb', displayName: 'Ben Website', email: 'ben@example.org', isActive: false, deleted: false },
      { id: 'pc', displayName: 'Cleo Zürcher', email: 'c.websitefan@example.org', isActive: true, deleted: false },
    ])
  })
  afterEach(async () => {
    await db.delete()
  })

  it('ranks the key, then a title starting with the query, a title containing it, the description', () => {
    const idea = { type: 'idea' as const, number: 42, title: 'Website chatbot', description: 'For customers' }
    expect(itemRank(idea, 'I-42')).toBe(0)
    expect(itemRank(idea, '42')).toBe(0)
    expect(itemRank(idea, 'website')).toBe(1)
    expect(itemRank(idea, 'chatbot')).toBe(2)
    expect(itemRank(idea, 'chatbot website')).toBe(3)
    expect(itemRank(idea, 'customers')).toBe(4)
    expect(itemRank(idea, 'website lead')).toBeNull()
    expect(itemRank(idea, '  ')).toBeNull()
    expect(personRank({ displayName: 'Cleo Zürcher', email: null }, 'zurch')).toBe(2)
    expect(personRank({ displayName: 'Cleo Zürcher', email: null }, 'cleo')).toBe(1)
  })

  it('finds every type without diacritics, best first, deleted items and inactive persons left out', async () => {
    const results = await searchLocal(db, 'website')
    expect(results.filter((r) => r.kind === 'item').map((r) => r.id)).toEqual(['l4', 'i1', 'p2', 'n3'])
    expect(results.filter((r) => r.kind === 'person').map((r) => r.id)).toEqual(['pc'])
    expect(results[0]).toMatchObject({
      kind: 'item',
      key: 'L-5',
      icon: 'pi pi-flag',
      to: { name: 'lead', params: { id: 'l4' } },
    })
    expect(results.find((r) => r.id === 'n3')!.subtitle).toContain('Zürich')
    expect((await searchLocal(db, 'zurich')).map((r) => r.id)).toEqual(['n3'])
    expect((await searchLocal(db, 'zurcher')).map((r) => r.id)).toEqual(['pc'])
    expect((await searchLocal(db, 'muller'))[0]!.id).toBe('l4')
    expect(await searchLocal(db, '')).toEqual([])
  })

  it('limits each kind', async () => {
    expect((await searchLocal(db, 'e', { limit: 2 })).filter((r) => r.kind === 'item')).toHaveLength(2)
  })
})
