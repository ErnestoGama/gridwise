<script setup lang="ts">
import { onMounted, ref } from 'vue'

interface HealthReport {
  status: string
  checks: Record<string, string>
}

type State =
  | { kind: 'loading' }
  | { kind: 'loaded'; report: HealthReport; httpStatus: number }
  | { kind: 'error'; message: string }

const state = ref<State>({ kind: 'loading' })

async function load(): Promise<void> {
  state.value = { kind: 'loading' }
  try {
    const response = await fetch('/health', { headers: { Accept: 'application/json' } })
    // 503 still carries a report describing which dependency is down.
    if (!response.ok && response.status !== 503) {
      throw new Error(`API answered ${response.status}`)
    }
    const report = (await response.json()) as HealthReport
    state.value = { kind: 'loaded', report, httpStatus: response.status }
  } catch (error) {
    state.value = {
      kind: 'error',
      message: error instanceof Error ? error.message : 'The API could not be reached',
    }
  }
}

onMounted(load)
</script>

<template>
  <section aria-labelledby="status-heading">
    <h1 id="status-heading">Service status</h1>

    <p v-if="state.kind === 'loading'" role="status">Checking the API…</p>

    <div v-else-if="state.kind === 'error'" role="alert">
      <p>The API is unreachable: {{ state.message }}</p>
      <button type="button" @click="load">Retry</button>
    </div>

    <template v-else>
      <p>
        Overall: <strong>{{ state.report.status }}</strong>
        (HTTP {{ state.httpStatus }})
      </p>
      <ul>
        <li v-for="(status, name) in state.report.checks" :key="name">
          {{ name }}: {{ status }}
        </li>
      </ul>
      <button type="button" @click="load">Refresh</button>
    </template>
  </section>
</template>
