import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { memoryDb } from '@/test/memoryDb'
import type { PgpmDb } from './db'
import { createDraftStore, mirrorKey } from './drafts'

describe('drafts (docs/03 §11.11): IndexedDB with a synchronous localStorage mirror', () => {
  let db: PgpmDb
  beforeEach(() => {
    db = memoryDb()
    localStorage.clear()
  })
  afterEach(async () => {
    await db.delete()
  })

  const draft = (text: string, savedAt = new Date().toISOString()) => ({ revision: 3, base: 'base', text, savedAt })

  it('writes, reads and removes a draft by its key', async () => {
    const store = createDraftStore(() => db)
    await store.write('items:n1:description', draft('typed'))
    expect(await store.read('items:n1:description')).toMatchObject({ text: 'typed', revision: 3, base: 'base' })
    await store.remove('items:n1:description')
    expect(await store.read('items:n1:description')).toBeNull()
  })

  it('mirrors the last draft synchronously when the tab goes, and promotes it on the next start', async () => {
    const store = createDraftStore(() => db)
    const write = store.write('items:n1:description', draft('almost lost'))
    store.flush()
    expect(JSON.parse(localStorage.getItem(mirrorKey)!)).toMatchObject({
      key: 'items:n1:description',
      draft: { text: 'almost lost' },
    })
    await write
    await db.drafts.clear()
    // Read before the promotion: the mirror answers.
    const next = createDraftStore(() => db)
    expect(await next.read('items:n1:description')).toMatchObject({ text: 'almost lost' })
    expect(await next.promote()).toBe('items:n1:description')
    expect(localStorage.getItem(mirrorKey)).toBeNull()
    expect(await db.drafts.get('items:n1:description')).toMatchObject({ text: 'almost lost' })
  })

  it('keeps a mirror too large for localStorage out of it', async () => {
    const store = createDraftStore(() => db)
    await store.write('items:n1:description', draft('x'.repeat(600 * 1024)))
    store.flush()
    expect(localStorage.getItem(mirrorKey)).toBeNull()
  })

  it('sweeps drafts older than 30 days and those of objects that are gone', async () => {
    const store = createDraftStore(() => db)
    const old = new Date(Date.now() - 31 * 86_400_000).toISOString()
    await store.write('items:old:description', draft('old', old))
    await store.write('items:gone:description', draft('gone'))
    await store.write('items:here:description', draft('here'))
    await store.write('new:note', draft('new'))
    const swept = await store.sweep(async (_table, id) => id !== 'gone')
    expect(swept.sort()).toEqual(['items:gone:description', 'items:old:description'])
    expect((await db.drafts.toArray()).map((d) => d.key).sort()).toEqual(['items:here:description', 'new:note'])
  })
})
