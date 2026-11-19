<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'

import { describeError } from '@/api/client'
import { useSessionStore } from '@/stores/session'

const session = useSessionStore()
const route = useRoute()
const router = useRouter()
const signOutError = ref('')

const currentOrganization = computed(() =>
  typeof route.params.organizationId === 'string' ? route.params.organizationId : '',
)

function switchOrganization(event: Event): void {
  const organizationId = (event.target as HTMLSelectElement).value
  void router.push({ name: 'buildings', params: { organizationId } })
}

async function signOut(): Promise<void> {
  signOutError.value = ''
  try {
    await session.signOut()
  } catch (error) {
    signOutError.value = describeError(error)
  }
  await router.replace({ name: 'login' })
}
</script>

<template>
  <header class="app-header">
    <span class="brand">GridWise</span>
    <nav v-if="session.me" aria-label="Main">
      <label class="sr-only" for="organization-select">Organization</label>
      <select
        v-if="session.me.memberships.length > 0"
        id="organization-select"
        :value="currentOrganization"
        @change="switchOrganization"
      >
        <option v-if="!currentOrganization" value="" disabled>Choose an organization</option>
        <option v-for="m in session.me.memberships" :key="m.organizationId" :value="m.organizationId">
          {{ m.organizationName }}
        </option>
      </select>
      <RouterLink :to="{ name: 'status' }">Status</RouterLink>
      <span class="who">{{ session.me.displayName }}</span>
      <button type="button" class="link" @click="signOut">Sign out</button>
    </nav>
  </header>
  <main>
    <p v-if="signOutError" class="alert" role="alert">{{ signOutError }}</p>
    <RouterView />
  </main>
</template>
