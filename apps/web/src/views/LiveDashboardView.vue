<script setup lang="ts">
import { computed, onBeforeUnmount, ref, toRef, watch } from 'vue'

import { ApiError } from '@/api/client'
import { getBuilding } from '@/api/sites'
import { getBuildingReadings } from '@/api/telemetry'
import type { BuildingDetail, MeterWindow, QualityFlag, ReadingsIngestedMessage } from '@/api/types'
import { useApiErrors } from '@/composables/useApiErrors'
import { chartRuns, contiguousRuns, mergeReadings, summarize, visibleFlags } from '@/dashboard/series'
import { useBuildingFeed, type FeedStatus } from '@/realtime/useBuildingFeed'

const props = defineProps<{ organizationId: string; buildingId: string }>()

const handleError = useApiErrors()

/** The dashboard shows a rolling day; the API allows up to 48 hours. */
const WINDOW_MS = 24 * 60 * 60 * 1000
const CHART_WIDTH = 480
const CHART_HEIGHT = 96

type State =
  | { kind: 'loading' }
  | { kind: 'loaded'; building: BuildingDetail }
  | { kind: 'missing' }
  | { kind: 'error'; message: string }

const state = ref<State>({ kind: 'loading' })
const meters = ref<MeterWindow[]>([])
const windowFrom = ref(new Date(Date.now() - WINDOW_MS))
const refreshError = ref('')

/** Loads the building and its readings; `quiet` keeps the current view while refreshing. */
async function load(quiet = false): Promise<void> {
  if (!quiet) {
    state.value = { kind: 'loading' }
  }
  const to = new Date()
  const from = new Date(to.getTime() - WINDOW_MS)
  try {
    const [building, readings] = await Promise.all([
      getBuilding(props.organizationId, props.buildingId),
      // One minute of slack so a reading that just started is not cut off by clock skew.
      getBuildingReadings(props.organizationId, props.buildingId, from, new Date(to.getTime() + 60_000)),
    ])
    windowFrom.value = from
    meters.value = readings.meters
    state.value = { kind: 'loaded', building }
    refreshError.value = ''
  } catch (error) {
    if (quiet && state.value.kind === 'loaded') {
      // Keep showing what we have; say that it may be stale.
      refreshError.value = handleError(error)
      return
    }
    // Buildings of other organizations also answer 404.
    state.value = error instanceof ApiError && error.status === 404
      ? { kind: 'missing' }
      : { kind: 'error', message: handleError(error) }
  }
}

let reloadTimer: ReturnType<typeof setTimeout> | undefined

/** Coalesces bursts of batches into one reload. */
function scheduleReload(): void {
  clearTimeout(reloadTimer)
  reloadTimer = setTimeout(() => void load(true), 1_500)
}

function applyMessage(message: ReadingsIngestedMessage): void {
  let unknownMeter = false
  const updated = meters.value.map((meter) => {
    const incoming = message.meters.find((m) => m.meterId === meter.meterId)
    return incoming
      ? { ...meter, readings: mergeReadings(meter.readings, incoming.readings, windowFrom.value) }
      : meter
  })
  for (const m of message.meters) {
    unknownMeter ||= !meters.value.some((meter) => meter.meterId === m.meterId)
  }
  meters.value = updated
  // Flags are not part of the message; a meter registered after the load is not either.
  if (message.qualityFlagsChanged || unknownMeter) {
    scheduleReload()
  }
}

const feed = useBuildingFeed(toRef(props, 'organizationId'), toRef(props, 'buildingId'), {
  onReadings: applyMessage,
  onResubscribed: () => scheduleReload(),
})

async function retryLive(): Promise<void> {
  if (await feed.retry()) {
    await load(true)
  }
}

// Slide the window every minute so old readings and flags leave the view.
const tick = setInterval(() => {
  const from = new Date(Date.now() - WINDOW_MS)
  windowFrom.value = from
  meters.value = meters.value.map((meter) => ({ ...meter, readings: mergeReadings(meter.readings, [], from) }))
}, 60_000)

onBeforeUnmount(() => {
  clearInterval(tick)
  clearTimeout(reloadTimer)
})

watch(
  () => [props.organizationId, props.buildingId],
  () => void load(),
  { immediate: true },
)

const timeZone = computed(() => (state.value.kind === 'loaded' ? state.value.building.timeZone : 'UTC'))

function formatTime(iso: string): string {
  try {
    return new Intl.DateTimeFormat(undefined, {
      timeZone: timeZone.value,
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    }).format(new Date(iso))
  } catch {
    return iso
  }
}

const kwh = (value: number): string =>
  `${value.toLocaleString(undefined, { minimumFractionDigits: 3, maximumFractionDigits: 3 })} kWh`

const panels = computed(() =>
  meters.value.map((meter) => {
    const summary = summarize(meter.readings, meter.intervalMinutes)
    const chart = chartRuns(
      contiguousRuns(meter.readings, meter.intervalMinutes),
      windowFrom.value,
      new Date(windowFrom.value.getTime() + WINDOW_MS),
      CHART_WIDTH,
      CHART_HEIGHT,
    )
    return { meter, summary, chart, flags: visibleFlags(meter.qualityFlags, windowFrom.value) }
  }),
)

function describeFlag(flag: QualityFlag): string {
  if (flag.kind === 'gap') {
    const count = flag.missingIntervals ?? 0
    return `${count} interval${count === 1 ? '' : 's'} unknown, ${formatTime(flag.intervalStart)} – ${formatTime(flag.intervalEnd)}`
  }
  const observed = flag.observedKwh === null ? '?' : kwh(flag.observedKwh)
  const baseline = flag.baselineKwh === null ? '?' : kwh(flag.baselineKwh)
  return `Spike at ${formatTime(flag.intervalStart)}: ${observed} against a median of ${baseline}`
}

const STATUS_TEXT: Record<FeedStatus, string> = {
  connecting: 'Connecting to live updates…',
  live: 'Live',
  reconnecting: 'Connection lost — reconnecting…',
  offline: 'Live updates stopped. Figures may be out of date.',
  denied: 'Live updates are not available for this building.',
}
</script>

<template>
  <section aria-labelledby="dashboard-heading">
    <p>
      <RouterLink :to="{ name: 'building', params: { organizationId, buildingId } }">← Building details</RouterLink>
    </p>

    <p v-if="state.kind === 'loading'" role="status">Loading dashboard…</p>

    <div v-else-if="state.kind === 'missing'" class="alert" role="alert">
      <h1 id="dashboard-heading">Building not found</h1>
      <p>It may have been removed, or it belongs to another organization.</p>
    </div>

    <div v-else-if="state.kind === 'error'" class="alert" role="alert">
      <h1 id="dashboard-heading">Live dashboard</h1>
      <p>{{ state.message }}</p>
      <button type="button" @click="load()">Retry</button>
    </div>

    <template v-else>
      <div class="dashboard-head">
        <h1 id="dashboard-heading">{{ state.building.name }} — last 24 hours</h1>
        <p class="feed" :class="`feed-${feed.status.value}`" role="status" aria-live="polite">
          <span class="feed-dot" aria-hidden="true" />
          {{ STATUS_TEXT[feed.status.value] }}
          <button
            v-if="feed.status.value === 'offline'"
            type="button"
            class="link"
            @click="retryLive"
          >
            Reconnect
          </button>
        </p>
      </div>
      <p class="muted">
        Times in {{ state.building.timeZone }}. Missing intervals are shown as breaks and counted as unknown,
        never as zero.
      </p>
      <p v-if="refreshError" class="alert" role="alert">Could not refresh: {{ refreshError }}</p>

      <p v-if="panels.length === 0" class="empty">This building has no meters yet.</p>

      <article
        v-for="{ meter, summary, chart, flags } in panels"
        :key="meter.meterId"
        class="panel meter-panel"
        :aria-labelledby="`meter-${meter.meterId}`"
      >
        <header class="meter-head">
          <h2 :id="`meter-${meter.meterId}`"><code>{{ meter.serial }}</code> <span class="muted">{{ meter.kind }}</span></h2>
          <p v-if="summary.latest" class="latest">
            {{ kwh(summary.latest.energyKwh) }}
            <span class="muted">at {{ formatTime(summary.latest.readingStart) }}</span>
          </p>
          <p v-else class="latest muted">No readings in the last 24 hours</p>
        </header>

        <svg
          v-if="summary.knownIntervals > 0"
          class="sparkline"
          :viewBox="`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`"
          preserveAspectRatio="none"
          role="img"
          :aria-label="`Energy per ${meter.intervalMinutes}-minute interval, up to ${kwh(chart.maxKwh)}`"
        >
          <template v-for="(line, index) in chart.lines" :key="index">
            <circle v-if="line.length === 1" :cx="line[0].x" :cy="line[0].y" r="2" />
            <polyline v-else :points="line.map((p) => `${p.x},${p.y}`).join(' ')" />
          </template>
        </svg>

        <dl class="facts">
          <dt>Known energy</dt>
          <dd>{{ kwh(summary.knownKwh) }} over {{ summary.knownIntervals }} intervals</dd>
          <dt>Unknown intervals</dt>
          <dd>{{ summary.unknownIntervals }}</dd>
        </dl>

        <h3 class="sr-only">Quality flags</h3>
        <ul v-if="flags.length > 0" class="flags">
          <li v-for="flag in flags" :key="flag.id" :class="`flag-${flag.kind}`">{{ describeFlag(flag) }}</li>
        </ul>
        <p v-else class="muted">No gaps or spikes flagged.</p>
      </article>
    </template>
  </section>
</template>
