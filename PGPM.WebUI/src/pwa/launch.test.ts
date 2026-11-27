import { describe, expect, it, vi } from 'vitest'
import type { Router } from 'vue-router'
import { installLaunchHandler } from './launch'

type Consumer = (params: { targetURL?: string }) => void

function fakeWindow(href: string) {
  let consumer: Consumer | null = null
  const win = {
    location: new URL(href),
    launchQueue: { setConsumer: (c: Consumer) => (consumer = c) },
  } as unknown as Window
  return { win, launch: (targetURL?: string) => consumer?.({ targetURL }) }
}

describe('launch_handler focus-existing (docs/03 §11.2)', () => {
  it('routes a shortcut opened while the app runs, not the launch that loaded it', () => {
    const push = vi.fn()
    const { win, launch } = fakeWindow('https://pgpm.example/maps')
    installLaunchHandler({ push } as unknown as Router, win)
    launch('https://pgpm.example/maps')
    expect(push).not.toHaveBeenCalled()
    launch('https://pgpm.example/notes?new=1')
    expect(push).toHaveBeenCalledWith('/notes?new=1')
    // Another origin, or no URL: nothing.
    launch('https://elsewhere.example/ideas?new=1')
    launch(undefined)
    expect(push).toHaveBeenCalledTimes(1)
  })

  it('does nothing where the browser has no launch queue', () => {
    const push = vi.fn()
    installLaunchHandler({ push } as unknown as Router, { location: new URL('https://pgpm.example/') } as never)
    expect(push).not.toHaveBeenCalled()
  })
})
