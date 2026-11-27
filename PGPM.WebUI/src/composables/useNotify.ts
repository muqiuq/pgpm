import { useToast } from 'primevue/usetoast'
import { ApiError } from '@/api/client'

/** Toasts with one voice: success briefly, errors until read. */
export function useNotify() {
  const toast = useToast()
  return {
    success(summary: string, detail?: string) {
      toast.add({ severity: 'success', summary, detail, life: 3000 })
    },
    info(summary: string, detail?: string) {
      toast.add({ severity: 'info', summary, detail, life: 5000 })
    },
    error(error: unknown, summary = 'Something went wrong') {
      const detail =
        error instanceof ApiError
          ? error.code === 'missing_right'
            ? 'You lack the right for this.'
            : error.message
          : error instanceof Error
            ? error.message
            : String(error)
      toast.add({ severity: 'error', summary, detail, life: 8000 })
    },
  }
}
