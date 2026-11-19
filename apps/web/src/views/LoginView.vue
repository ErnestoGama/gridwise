<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { ApiError, describeError } from '@/api/client'
import { useSessionStore } from '@/stores/session'

const session = useSessionStore()
const route = useRoute()
const router = useRouter()

const email = ref('')
const password = ref('')
const submitting = ref(false)
const failure = ref<ApiError | null>(null)
const message = ref('')

/** Only same-app paths are followed after sign-in. */
function nextPath(): string {
  const next = route.query.next
  return typeof next === 'string' && next.startsWith('/') && !next.startsWith('//') ? next : '/'
}

async function submit(): Promise<void> {
  submitting.value = true
  failure.value = null
  message.value = ''
  try {
    await session.signIn(email.value.trim(), password.value)
    password.value = ''
    await router.replace(nextPath())
  } catch (error) {
    failure.value = error instanceof ApiError ? error : null
    message.value = describeError(error)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <section class="narrow" aria-labelledby="login-heading">
    <h1 id="login-heading">Sign in</h1>

    <p v-if="message" class="alert" role="alert">{{ message }}</p>

    <form novalidate @submit.prevent="submit">
      <label for="login-email">Email</label>
      <input
        id="login-email"
        v-model="email"
        type="email"
        autocomplete="username"
        required
        :aria-invalid="!!failure?.field('email')"
        aria-describedby="login-email-error"
      />
      <p id="login-email-error" class="field-error">{{ failure?.field('email') }}</p>

      <label for="login-password">Password</label>
      <input
        id="login-password"
        v-model="password"
        type="password"
        autocomplete="current-password"
        required
        :aria-invalid="!!failure?.field('password')"
        aria-describedby="login-password-error"
      />
      <p id="login-password-error" class="field-error">{{ failure?.field('password') }}</p>

      <button type="submit" :disabled="submitting">
        {{ submitting ? 'Signing in…' : 'Sign in' }}
      </button>
    </form>
  </section>
</template>
