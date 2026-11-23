import type { LiveReading, QualityFlag } from '@/api/types'

/**
 * Pure helpers for the live dashboard. Readings are kept sorted by start time
 * and unique per interval; a missing interval is never turned into zero.
 */

const time = (iso: string): number => Date.parse(iso)

/**
 * Adds live readings to a series. A reading for an interval already held is
 * ignored (stored readings are immutable), readings before `from` are dropped
 * and the result stays in time order.
 */
export function mergeReadings(current: LiveReading[], incoming: LiveReading[], from: Date): LiveReading[] {
  const cutoff = from.getTime()
  const byStart = new Map<number, LiveReading>()
  for (const reading of current) {
    byStart.set(time(reading.readingStart), reading)
  }
  for (const reading of incoming) {
    const start = time(reading.readingStart)
    if (!byStart.has(start)) {
      byStart.set(start, reading)
    }
  }
  return [...byStart.entries()]
    .filter(([start]) => start >= cutoff)
    .sort(([a], [b]) => a - b)
    .map(([, reading]) => reading)
}

/**
 * Splits a series into runs of back-to-back intervals. A chart draws each run
 * as its own line, so a gap shows as a break instead of a drop to zero.
 */
export function contiguousRuns(readings: LiveReading[], intervalMinutes: number): LiveReading[][] {
  const step = intervalMinutes * 60_000
  const runs: LiveReading[][] = []
  let run: LiveReading[] = []
  let previous: number | null = null
  for (const reading of readings) {
    const start = time(reading.readingStart)
    if (previous !== null && start - previous !== step) {
      runs.push(run)
      run = []
    }
    run.push(reading)
    previous = start
  }
  if (run.length > 0) {
    runs.push(run)
  }
  return runs
}

export interface SeriesSummary {
  /** Sum of the known intervals only. */
  knownKwh: number
  knownIntervals: number
  /** Intervals between the first and last reading that have no reading. */
  unknownIntervals: number
  latest: LiveReading | null
}

export function summarize(readings: LiveReading[], intervalMinutes: number): SeriesSummary {
  if (readings.length === 0) {
    return { knownKwh: 0, knownIntervals: 0, unknownIntervals: 0, latest: null }
  }
  const step = intervalMinutes * 60_000
  const first = time(readings[0].readingStart)
  const last = time(readings[readings.length - 1].readingStart)
  const expected = Math.round((last - first) / step) + 1
  // Sum in thousandths so three-decimal kWh values add up exactly.
  const milli = readings.reduce((sum, r) => sum + Math.round(r.energyKwh * 1000), 0)
  return {
    knownKwh: milli / 1000,
    knownIntervals: readings.length,
    unknownIntervals: Math.max(0, expected - readings.length),
    latest: readings[readings.length - 1],
  }
}

export interface ChartPoint {
  x: number
  y: number
}

/**
 * Scales runs to an SVG box. The x axis is the window, the y axis starts at
 * zero and ends at the largest reading (at least 1 kWh, so a flat line is not
 * stretched to the top).
 */
export function chartRuns(
  runs: LiveReading[][],
  from: Date,
  to: Date,
  width: number,
  height: number,
): { lines: ChartPoint[][]; maxKwh: number } {
  const start = from.getTime()
  const span = Math.max(1, to.getTime() - start)
  const maxKwh = Math.max(1, ...runs.flat().map((r) => r.energyKwh))
  const lines = runs.map((run) =>
    run.map((r) => ({
      x: round(((time(r.readingStart) - start) / span) * width),
      y: round(height - (r.energyKwh / maxKwh) * height),
    })),
  )
  return { lines, maxKwh }
}

/** Flags that still overlap the window, newest first, for the side list. */
export function visibleFlags(flags: QualityFlag[], from: Date): QualityFlag[] {
  const cutoff = from.getTime()
  return flags
    .filter((f) => time(f.intervalEnd) > cutoff)
    .sort((a, b) => time(b.intervalStart) - time(a.intervalStart))
}

const round = (value: number): number => Math.round(value * 10) / 10
