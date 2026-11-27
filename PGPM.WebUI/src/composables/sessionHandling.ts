import { watch } from 'vue'
import type { Router } from 'vue-router'
import { onApiError } from '@/api/client'
import { queryClient } from '@/api/queries'
import { useDailyTotp } from '@/composables/useDailyTotp'
import { useSession } from '@/stores/session'

/**
 * What every failed call means for the session (PNMTD): a 401 outside the sign-in ends it and opens the sign-in with the
 * current page as returnUrl (and why, after Auth:LoginDays); 403 totp_due opens the day's code over the page; 403
 * must_change_password or totp_setup_required (another tab, a policy change) reloads the flags, and the guard opens the
 * matching screen. A session that ends in the background check (sessionStore.refresh) goes the same way.
 */
export function installSessionHandling(router: Router) {
  const session = useSession()
  const daily = useDailyTotp()

  async function toSignIn(reason?: string) {
    queryClient.clear()
    const current = router.currentRoute.value
    if (current.meta.public) return
    const query: Record<string, string> = { returnUrl: current.fullPath }
    if (reason) query.reason = reason
    await router.replace({ name: 'sign-in', query })
  }

  onApiError(async (error, path) => {
    if (path.startsWith('/api/v1/auth/')) {
      return
    }
    if (error.status === 401 && session.user) {
      session.set(null)
      await toSignIn(error.code === 'login_expired' ? 'expired' : undefined)
    } else if (error.status === 403 && error.code === 'totp_due') {
      daily.ask()
    } else if (
      error.status === 403 &&
      (error.code === 'must_change_password' || error.code === 'totp_setup_required')
    ) {
      await session.load(true)
      await router.replace(router.currentRoute.value.fullPath)
    }
  })

  watch(
    () => session.user,
    async (user, before) => {
      if (!user && before) await toSignIn()
    },
  )
  watch(
    () => session.times?.totpDue,
    (due) => {
      if (due) daily.ask()
    },
    { immediate: true },
  )
}
