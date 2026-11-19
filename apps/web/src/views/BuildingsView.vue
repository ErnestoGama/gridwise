<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'

import { ApiError } from '@/api/client'
import { createBuilding, listBuildings } from '@/api/sites'
import type { Building } from '@/api/types'
import { useApiErrors } from '@/composables/useApiErrors'
import { useSessionStore } from '@/stores/session'

const props = defineProps<{ organizationId: string }>()

const session = useSessionStore()
const handleError = useApiErrors()

const membership = computed(() => session.membership(props.organizationId))
const canCreate = computed(() => session.isAdmin(props.organizationId))
const timeZones = Intl.supportedValuesOf('timeZone')

type ListState = { kind: 'loading' } | { kind: 'loaded' } | { kind: 'error'; message: string }

const buildings = ref<Building[]>([])
const nextCursor = ref<string | null>(null)
const state = ref<ListState>({ kind: 'loading' })
const loadingMore = ref(false)
const moreError = ref('')

async function load(): Promise<void> {
  state.value = { kind: 'loading' }
  try {
    const page = await listBuildings(props.organizationId)
    buildings.value = page.items
    nextCursor.value = page.nextCursor
    state.value = { kind: 'loaded' }
  } catch (error) {
    state.value = {
      kind: 'error',
      message: error instanceof ApiError && error.status === 404 ? 'Organization not found.' : handleError(error),
    }
  }
}

async function loadMore(): Promise<void> {
  if (!nextCursor.value) {
    return
  }
  loadingMore.value = true
  moreError.value = ''
  try {
    const page = await listBuildings(props.organizationId, nextCursor.value)
    buildings.value.push(...page.items)
    nextCursor.value = page.nextCursor
  } catch (error) {
    moreError.value = handleError(error)
  } finally {
    loadingMore.value = false
  }
}

// v-model on a number input yields a number, or '' when the box is empty.
const form = reactive<{ name: string; timeZone: string; floorArea: number | string }>({
  name: '',
  timeZone: '',
  floorArea: '',
})
const saving = ref(false)
const formError = ref<ApiError | null>(null)
const formMessage = ref('')
const created = ref('')

function resetForm(): void {
  form.name = ''
  form.timeZone = ''
  form.floorArea = ''
}

async function submit(): Promise<void> {
  saving.value = true
  formError.value = null
  formMessage.value = ''
  created.value = ''
  const area = String(form.floorArea).trim()
  if (area !== '' && !Number.isInteger(Number(area))) {
    // The API takes whole square metres; a fraction would not deserialize.
    formError.value = new ApiError(422, 'validation_failed', '', null, {
      floorAreaM2: ['Floor area must be a whole number of m².'],
    })
    formMessage.value = 'One or more fields are invalid.'
    saving.value = false
    return
  }
  try {
    const building = await createBuilding(props.organizationId, {
      name: form.name.trim(),
      timeZone: form.timeZone.trim(),
      floorAreaM2: area === '' ? null : Number(area),
    })
    created.value = `${building.name} was added.`
    resetForm()
    await load()
  } catch (error) {
    formError.value = error instanceof ApiError ? error : null
    formMessage.value = handleError(error)
  } finally {
    saving.value = false
  }
}

function formatArea(area: number | null): string {
  return area === null ? '—' : `${area.toLocaleString()} m²`
}

watch(
  () => props.organizationId,
  () => {
    resetForm()
    formError.value = null
    formMessage.value = ''
    created.value = ''
    void load()
  },
  { immediate: true },
)
</script>

<template>
  <section aria-labelledby="buildings-heading">
    <h1 id="buildings-heading">Buildings</h1>
    <p v-if="membership" class="muted">
      {{ membership.organizationName }} · your role: {{ membership.role }}
    </p>

    <p v-if="state.kind === 'loading'" role="status">Loading buildings…</p>

    <div v-else-if="state.kind === 'error'" class="alert" role="alert">
      <p>{{ state.message }}</p>
      <button type="button" @click="load">Retry</button>
    </div>

    <template v-else>
      <p v-if="buildings.length === 0" class="empty">
        No buildings yet.
        <span v-if="canCreate">Add the first one below.</span>
        <span v-else>An organization admin can add them.</span>
      </p>

      <table v-else>
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Time zone</th>
            <th scope="col">Floor area</th>
            <th scope="col">Meters</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="building in buildings" :key="building.id">
            <td>
              <RouterLink :to="{ name: 'building', params: { organizationId, buildingId: building.id } }">
                {{ building.name }}
              </RouterLink>
            </td>
            <td>{{ building.timeZone }}</td>
            <td>{{ formatArea(building.floorAreaM2) }}</td>
            <td>{{ building.meterCount }}</td>
          </tr>
        </tbody>
      </table>

      <p v-if="moreError" class="alert" role="alert">{{ moreError }}</p>
      <button v-if="nextCursor" type="button" :disabled="loadingMore" @click="loadMore">
        {{ loadingMore ? 'Loading…' : 'Load more' }}
      </button>
    </template>

    <section v-if="canCreate && state.kind === 'loaded'" class="panel" aria-labelledby="new-building-heading">
      <h2 id="new-building-heading">Add a building</h2>

      <p v-if="created" class="success" role="status">{{ created }}</p>
      <p v-if="formMessage" class="alert" role="alert">{{ formMessage }}</p>

      <form novalidate @submit.prevent="submit">
        <label for="building-name">Name</label>
        <input
          id="building-name"
          v-model="form.name"
          maxlength="120"
          required
          :aria-invalid="!!formError?.field('name')"
          aria-describedby="building-name-error"
        />
        <p id="building-name-error" class="field-error">{{ formError?.field('name') }}</p>

        <label for="building-time-zone">Time zone (IANA, e.g. Europe/Madrid)</label>
        <input
          id="building-time-zone"
          v-model="form.timeZone"
          list="time-zones"
          required
          :aria-invalid="!!formError?.field('timeZone')"
          aria-describedby="building-time-zone-error"
        />
        <datalist id="time-zones">
          <option v-for="zone in timeZones" :key="zone" :value="zone" />
        </datalist>
        <p id="building-time-zone-error" class="field-error">{{ formError?.field('timeZone') }}</p>

        <label for="building-area">Floor area in m² (optional)</label>
        <input
          id="building-area"
          v-model="form.floorArea"
          type="number"
          inputmode="numeric"
          min="1"
          max="2000000"
          step="1"
          :aria-invalid="!!formError?.field('floorAreaM2')"
          aria-describedby="building-area-error"
        />
        <p id="building-area-error" class="field-error">{{ formError?.field('floorAreaM2') }}</p>

        <button type="submit" :disabled="saving">{{ saving ? 'Saving…' : 'Add building' }}</button>
      </form>
    </section>
  </section>
</template>
