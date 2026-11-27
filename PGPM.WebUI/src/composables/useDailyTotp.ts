import { ref } from 'vue'

/**
 * The day's 2FA code (PNMTD, docs/03 §4.2): a 403 totp_due anywhere – or a session that says so – opens the dialog over
 * the current page, so nothing typed is lost. After the code, what failed is fetched again.
 */
const open = ref(false)

export function useDailyTotp() {
  return {
    open,
    ask: () => {
      open.value = true
    },
    close: () => {
      open.value = false
    },
  }
}
