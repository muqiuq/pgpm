import { describe, expect, it } from 'vitest'
import { agent, bytes, duration } from './systemFormat'

describe('systemFormat', () => {
  it('writes sizes in binary units', () => {
    expect(bytes(null)).toBe('–')
    expect(bytes(1)).toBe('1 byte')
    expect(bytes(900)).toBe('900 bytes')
    expect(bytes(1536)).toBe('1.5 KB')
    expect(bytes(25 * 1024 * 1024)).toBe('25 MB')
    expect(bytes(1.25 * 1024 ** 3)).toBe('1.3 GB')
    expect(bytes(512 * 1024 ** 3)).toBe('512 GB')
  })

  it('writes the uptime in its two largest units', () => {
    expect(duration(42)).toBe('42 s')
    expect(duration(125)).toBe('2 min')
    expect(duration(2 * 3600 + 5 * 60)).toBe('2 h 5 min')
    expect(duration(3 * 86_400 + 4 * 3600 + 59)).toBe('3 d 4 h')
    expect(duration(3600)).toBe('1 h')
    expect(duration(86_400)).toBe('1 d')
  })

  it('names the browser and the system of a user agent', () => {
    expect(agent(null)).toBe('–')
    expect(
      agent(
        'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15',
      ),
    ).toBe('Safari on macOS')
    expect(
      agent('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36 Edg/131.0'),
    ).toBe('Edge on Windows')
    expect(agent('Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0')).toBe('Firefox on Linux')
    expect(agent('test')).toBe('test')
  })
})
