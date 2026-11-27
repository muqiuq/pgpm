import { computed, ref } from 'vue'

/**
 * Installing the app (PLM's ui/install.js, docs/03 §11.2), offered in the user menu. Chromium fires
 * `beforeinstallprompt` once, early, and only hands the prompt to a user gesture, so it is caught at module load (main.ts
 * imports this first) and kept. Safari has no such event: on iOS installing is Share → Add to Home Screen, so there are
 * instructions instead of a button that does nothing.
 *
 *   standalone – running as the installed app
 *   ready      – the browser offered a prompt we can replay
 *   installed  – installed during this session
 *   manual     – no prompt: the browser's own menu (or the iOS steps)
 */
export type InstallStatus = 'standalone' | 'ready' | 'installed' | 'manual'

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

const deferred = ref<InstallPromptEvent | null>(null)
const installed = ref(false)

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (event) => {
    // Suppressing the browser's own mini-infobar is the price of prompting later from our own entry.
    event.preventDefault()
    deferred.value = event as InstallPromptEvent
  })
  window.addEventListener('appinstalled', () => {
    installed.value = true
    deferred.value = null
  })
}

/** True when this document is the installed app rather than a browser tab. */
export function isStandalone(): boolean {
  return (
    window.matchMedia?.('(display-mode: standalone)').matches ||
    window.matchMedia?.('(display-mode: minimal-ui)').matches ||
    // iOS predates display-mode and says so here.
    (navigator as Navigator & { standalone?: boolean }).standalone === true
  )
}

/** iPhone, iPad, and an iPad that calls itself a Mac. */
export const isIos = () =>
  /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)

export const installStatus = computed<InstallStatus>(() => {
  if (isStandalone()) return 'standalone'
  if (deferred.value) return 'ready'
  if (installed.value) return 'installed'
  return 'manual'
})

/** The browser's install prompt; from a user gesture only. */
export async function promptInstall(): Promise<'accepted' | 'dismissed' | 'unavailable'> {
  const prompt = deferred.value
  if (!prompt) return 'unavailable'
  // Single-use whatever the outcome: dropped first, so a second click can't call prompt() twice.
  deferred.value = null
  try {
    await prompt.prompt()
    const { outcome } = await prompt.userChoice
    return outcome
  } catch {
    return 'unavailable'
  }
}

/** What to do where there is no prompt. */
export function manualInstallSteps(): string {
  return isIos()
    ? 'In Safari: Share → Add to Home Screen.'
    : "Use the browser's menu: Install PGPM (Chrome, Edge) or Add to Dock (Safari on macOS)."
}
