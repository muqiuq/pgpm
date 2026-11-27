import { describe, expect, it, vi } from 'vitest'
import { requestPersistence } from './storage'

describe('requestPersistence (docs/03 §11.2)', () => {
  const storage = (persisted: boolean, persist: boolean | Error) =>
    ({
      persisted: vi.fn(async () => persisted),
      persist: vi.fn(async () => {
        if (persist instanceof Error) throw persist
        return persist
      }),
    }) as unknown as StorageManager

  it('asks only when not granted yet, and says what the browser answered', async () => {
    const granted = storage(true, false)
    expect(await requestPersistence(granted)).toBe('granted')
    expect(granted.persist).not.toHaveBeenCalled()
    expect(await requestPersistence(storage(false, true))).toBe('granted')
    expect(await requestPersistence(storage(false, false))).toBe('denied')
  })

  it('is unsupported without the API or when it throws', async () => {
    expect(await requestPersistence({} as StorageManager)).toBe('unsupported')
    expect(await requestPersistence(storage(false, new Error('nope')))).toBe('unsupported')
  })
})
