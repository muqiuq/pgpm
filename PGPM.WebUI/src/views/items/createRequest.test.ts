import { describe, expect, it } from 'vitest'
import { createRequested, takeCreateRequest } from './createRequest'

describe('New note / New idea from a link (the installed app’s shortcuts)', () => {
  it('takes ?new=1 off a list’s URL and leaves the request for the list', () => {
    createRequested.value = null
    expect(takeCreateRequest({ name: 'notes', path: '/notes', query: { new: '1', q: 'pilot' }, hash: '' })).toEqual({
      path: '/notes',
      query: { q: 'pilot' },
      hash: '',
      replace: true,
    })
    expect(createRequested.value).toBe('note')
    expect(takeCreateRequest({ name: 'ideas', path: '/ideas', query: { new: '1' }, hash: '#x' })).toMatchObject({
      path: '/ideas',
      query: {},
      hash: '#x',
    })
    expect(createRequested.value).toBe('idea')
  })

  it('leaves every other route alone', () => {
    createRequested.value = null
    expect(takeCreateRequest({ name: 'notes', path: '/notes', query: {}, hash: '' })).toBeNull()
    expect(takeCreateRequest({ name: 'maps', path: '/maps', query: { new: '1' }, hash: '' })).toBeNull()
    expect(takeCreateRequest({ name: undefined, path: '/x', query: { new: '1' }, hash: '' })).toBeNull()
    expect(createRequested.value).toBeNull()
  })
})
