<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'

import { ApiError } from '@/api/client'
import { createMeter, getBuilding } from '@/api/sites'
import { DEFAULT_INTERVAL_MINUTES, METER_INTERVALS, METER_KINDS, type BuildingDetail } from '@/api/types'
import { useApiErrors } from '@/composables/useApiErrors'
import { useSessionStore } from '@/stores/session'

const props = defineProps<{ organizationId: string; buildingId: string }>()

const session = useSessionStore()
const handleError = useApiErrors()
const canCreate = computed(() => session.isAdmin(props.organizationId))

type State =
  | { kind: 'loading' }
  | { kind: 'loaded'; building: BuildingDetail }
  | { kind: 'missing' }
  | { kind: 'error'; message: string }

const state = ref<State>({ kind: 'loading' })

async function load(): Promise<void> {
  state.value = { kind: 'loading' }
  try {
    state.value = { kind: 'loaded', building: await getBuilding(props.organizationId, props.buildingId) }
  } catch (error) {
    // Buildings of other organizations also answer 404.
    state.value = error instanceof ApiError && error.status === 404
      ? { kind: 'missing' }
      : { kind: 'error', message: handleError(error) }
  }
}

/** Local wall-clock time at the building, so operators see the site's own day. */
function localTime(timeZone: string): string {
  try {
    return new Intl.DateTimeFormat(undefined, { timeZone, dateStyle: 'medium', timeStyle: 'short' }).format(new Date())
  } catch {
    return '—'
  }
}

const form = reactive({ serial: '', kind: 'main', intervalMinutes: DEFAULT_INTERVAL_MINUTES })
const saving = ref(false)
const formError = ref<ApiError | null>(null)
const formMessage = ref('')
const created = ref('')

function resetForm(): void {
  form.serial = ''
  form.kind = 'main'
  form.intervalMinutes = DEFAULT_INTERVAL_MINUTES
}

async function submit(): Promise<void> {
  saving.value = true
  formError.value = null
  formMessage.value = ''
  created.value = ''
  try {
    const meter = await createMeter(props.organizationId, {
      buildingId: props.buildingId,
      serial: form.serial.trim(),
      kind: form.kind,
      intervalMinutes: form.intervalMinutes,
    })
    created.value = `Meter ${meter.serial} was added.`
    resetForm()
    await load()
  } catch (error) {
    formError.value = error instanceof ApiError ? error : null
    formMessage.value = handleError(error)
  } finally {
    saving.value = false
  }
}

watch(
  () => [props.organizationId, props.buildingId],
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
  <section aria-labelledby="building-heading">
    <p>
      <RouterLink :to="{ name: 'buildings', params: { organizationId } }">← All buildings</RouterLink>
    </p>

    <p v-if="state.kind === 'loading'" role="status">Loading building…</p>

    <div v-else-if="state.kind === 'missing'" class="alert" role="alert">
      <h1 id="building-heading">Building not found</h1>
      <p>It may have been removed, or it belongs to another organization.</p>
    </div>

    <div v-else-if="state.kind === 'error'" class="alert" role="alert">
      <h1 id="building-heading">Building</h1>
      <p>{{ state.message }}</p>
      <button type="button" @click="load">Retry</button>
    </div>

    <template v-else>
      <h1 id="building-heading">{{ state.building.name }}</h1>
      <p>
        <RouterLink :to="{ name: 'building-live', params: { organizationId, buildingId } }">Live dashboard →</RouterLink>
      </p>
      <dl class="facts">
        <dt>Time zone</dt>
        <dd>{{ state.building.timeZone }} (now {{ localTime(state.building.timeZone) }})</dd>
        <dt>Floor area</dt>
        <dd>{{ state.building.floorAreaM2 === null ? '—' : `${state.building.floorAreaM2.toLocaleString()} m²` }}</dd>
      </dl>

      <h2>Meters</h2>
      <p v-if="state.building.meters.length === 0" class="empty">
        No meters yet.
        <span v-if="canCreate">Register one below.</span>
        <span v-else>An organization admin can register them.</span>
      </p>
      <table v-else>
        <thead>
          <tr>
            <th scope="col">Serial</th>
            <th scope="col">Kind</th>
            <th scope="col">Interval</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="meter in state.building.meters" :key="meter.id">
            <td><code>{{ meter.serial }}</code></td>
            <td>{{ meter.kind }}</td>
            <td>{{ meter.intervalMinutes }} min</td>
          </tr>
        </tbody>
      </table>

      <section v-if="canCreate" class="panel" aria-labelledby="new-meter-heading">
        <h2 id="new-meter-heading">Register a meter</h2>

        <p v-if="created" class="success" role="status">{{ created }}</p>
        <p v-if="formMessage" class="alert" role="alert">{{ formMessage }}</p>
        <p v-if="formError?.field('buildingId')" class="field-error">{{ formError.field('buildingId') }}</p>

        <form novalidate @submit.prevent="submit">
          <label for="meter-serial">Serial (letters, digits and hyphens)</label>
          <input
            id="meter-serial"
            v-model="form.serial"
            maxlength="40"
            autocapitalize="characters"
            required
            :aria-invalid="!!formError?.field('serial')"
            aria-describedby="meter-serial-error"
          />
          <p id="meter-serial-error" class="field-error">{{ formError?.field('serial') }}</p>

          <label for="meter-kind">Kind</label>
          <select
            id="meter-kind"
            v-model="form.kind"
            :aria-invalid="!!formError?.field('kind')"
            aria-describedby="meter-kind-error"
          >
            <option v-for="kind in METER_KINDS" :key="kind" :value="kind">{{ kind }}</option>
          </select>
          <p id="meter-kind-error" class="field-error">{{ formError?.field('kind') }}</p>

          <label for="meter-interval">Reading interval</label>
          <select
            id="meter-interval"
            v-model.number="form.intervalMinutes"
            :aria-invalid="!!formError?.field('intervalMinutes')"
            aria-describedby="meter-interval-error"
          >
            <option v-for="minutes in METER_INTERVALS" :key="minutes" :value="minutes">{{ minutes }} minutes</option>
          </select>
          <p id="meter-interval-error" class="field-error">{{ formError?.field('intervalMinutes') }}</p>

          <button type="submit" :disabled="saving">{{ saving ? 'Saving…' : 'Register meter' }}</button>
        </form>
      </section>
    </template>
  </section>
</template>
