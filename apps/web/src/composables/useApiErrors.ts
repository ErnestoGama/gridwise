import { useRoute, useRouter } from 'vue-router'

import { ApiError, UnauthenticatedError, describeError } from '@/api/client'
import { useSessionStore } from '@/stores/session'

/**
 * Turns a thrown API error into text for the page. An expired session sends
 * the user to sign-in and comes back here afterwards.
 */
export function useApiErrors() {
  const session = useSessionStore()
  const route = useRoute()
  const router = useRouter()

  return function handle(error: unknown): string {
    if (error instanceof UnauthenticatedError) {
      session.forget()
      void router.replace({ name: 'login', query: { next: route.fullPath } })
      return 'Your session has ended. Sign in again.'
    }
    if (error instanceof ApiError && error.status === 403) {
      return 'Your role does not allow this action. Ask an organization admin.'
    }
    return describeError(error)
  }
}
