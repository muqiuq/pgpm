import type { Router } from 'vue-router'

interface LaunchParams {
  targetURL?: string
}

interface LaunchQueue {
  setConsumer(consumer: (params: LaunchParams) => void): void
}

/**
 * The manifest's `launch_handler: focus-existing` (docs/03 §11.2): a shortcut (*New note*, *New idea*) or a link opened
 * while the installed app runs focuses its window instead of opening another, and the browser hands the URL to the
 * launch queue – the router goes there. The launch that loaded this document is the page itself already. Chromium
 * only; elsewhere nothing changes (a new window opens at the URL).
 */
export function installLaunchHandler(router: Router, win: Window = window): void {
  const queue = (win as Window & { launchQueue?: LaunchQueue }).launchQueue
  if (!queue) return
  const loaded = win.location.href
  let first = true
  queue.setConsumer((params) => {
    const target = params.targetURL
    const cold = first && target === loaded
    first = false
    if (!target || cold) return
    let url: URL
    try {
      url = new URL(target)
    } catch {
      return
    }
    if (url.origin !== win.location.origin) return
    void router.push(`${url.pathname}${url.search}${url.hash}`)
  })
}
