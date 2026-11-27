import { QueryClient } from '@tanstack/vue-query'
import { ApiError } from './client'

/**
 * Online-only server state (docs/03 §2.2: history, audit log, admin lists – synced data lives in IndexedDB from phase 2):
 * refetched when the window regains focus; a client error (4xx) is not retried, a lost connection twice.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      refetchOnWindowFocus: true,
      retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
    },
    mutations: { retry: false },
  },
})
