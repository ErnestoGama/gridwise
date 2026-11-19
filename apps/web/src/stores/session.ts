import { computed, ref } from 'vue'
import { defineStore } from 'pinia'

import { UnauthenticatedError } from '@/api/client'
import * as sites from '@/api/sites'
import type { Me, Membership } from '@/api/types'

/** Signed-in user and their organizations. The session itself is an HttpOnly cookie. */
export const useSessionStore = defineStore('session', () => {
  const me = ref<Me | null>(null)
  const checked = ref(false)

  const signedIn = computed(() => me.value !== null)

  /** Loads the current user once; a 401 just means nobody is signed in. */
  async function ensureLoaded(): Promise<void> {
    if (checked.value) {
      return
    }
    try {
      me.value = await sites.getMe()
    } catch (error) {
      if (!(error instanceof UnauthenticatedError)) {
        throw error
      }
      me.value = null
    }
    checked.value = true
  }

  async function signIn(email: string, password: string): Promise<void> {
    me.value = await sites.login(email, password)
    checked.value = true
  }

  async function signOut(): Promise<void> {
    try {
      await sites.logout()
    } finally {
      forget()
    }
  }

  /** Drops local state, e.g. after the API answered 401 for an expired cookie. */
  function forget(): void {
    me.value = null
    checked.value = true
  }

  function membership(organizationId: string): Membership | undefined {
    return me.value?.memberships.find((m) => m.organizationId === organizationId)
  }

  function isAdmin(organizationId: string): boolean {
    return membership(organizationId)?.role === 'admin'
  }

  return { me, signedIn, ensureLoaded, signIn, signOut, forget, membership, isAdmin }
})
