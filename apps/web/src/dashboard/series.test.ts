import { describe, expect, it } from 'vitest'

import type { LiveReading, QualityFlag } from '@/api/types'

import { chartRuns, contiguousRuns, mergeReadings, summarize, visibleFlags } from './series'

const at = (minute: number): string => new Date(Date.UTC(2026, 2, 29, 0, minute)).toISOString()
const reading = (minute: number, energyKwh = 1): LiveReading => ({
  sequence: minute,
  readingStart: at(minute),
  energyKwh,
})
const from = new Date(at(0))

describe('mergeReadings', () => {
  it('keeps time order, ignores intervals already held and drops readings before the window', () => {
    const current = [reading(0, 1), reading(30, 3)]
    const incoming = [reading(15, 2), reading(30, 99), reading(-15, 5)]

    const merged = mergeReadings(current, incoming, from)

    expect(merged.map((r) => r.readingStart)).toEqual([at(0), at(15), at(30)])
    expect(merged[2].energyKwh).toBe(3)
  })

  it('treats the same instant written with another offset as the same interval', () => {
    const current = [reading(0)]
    const sameInstant = { ...reading(0, 7), readingStart: '2026-03-29T02:00:00+02:00' }

    expect(mergeReadings(current, [sameInstant], from)).toHaveLength(1)
  })
})

describe('contiguousRuns', () => {
  it('breaks the series where intervals are missing', () => {
    const runs = contiguousRuns([reading(0), reading(15), reading(60), reading(75), reading(120)], 15)

    expect(runs.map((run) => run.map((r) => r.sequence))).toEqual([[0, 15], [60, 75], [120]])
  })

  it('returns no runs for an empty series', () => {
    expect(contiguousRuns([], 15)).toEqual([])
  })
})

describe('summarize', () => {
  it('sums only known intervals and counts the missing ones between them', () => {
    const summary = summarize([reading(0, 0.1), reading(15, 0.2), reading(60, 0.3)], 15)

    expect(summary.knownKwh).toBe(0.6)
    expect(summary.knownIntervals).toBe(3)
    expect(summary.unknownIntervals).toBe(2)
    expect(summary.latest?.sequence).toBe(60)
  })

  it('reports nothing for a meter without readings', () => {
    expect(summarize([], 15)).toEqual({ knownKwh: 0, knownIntervals: 0, unknownIntervals: 0, latest: null })
  })
})

describe('chartRuns', () => {
  it('scales readings to the box with zero at the bottom', () => {
    const to = new Date(at(60))
    const { lines, maxKwh } = chartRuns([[reading(0, 2), reading(15, 4)], [reading(60, 0)]], from, to, 100, 50)

    expect(maxKwh).toBe(4)
    expect(lines).toEqual([
      [{ x: 0, y: 25 }, { x: 25, y: 0 }],
      [{ x: 100, y: 50 }],
    ])
  })

  it('does not stretch small values to the top', () => {
    const { maxKwh } = chartRuns([[reading(0, 0.2)]], from, new Date(at(60)), 100, 50)
    expect(maxKwh).toBe(1)
  })
})

describe('visibleFlags', () => {
  const flag = (kind: 'gap' | 'spike', start: number, end: number): QualityFlag => ({
    id: `${kind}-${start}`,
    kind,
    intervalStart: at(start),
    intervalEnd: at(end),
    missingIntervals: kind === 'gap' ? (end - start) / 15 : null,
    sequence: kind === 'spike' ? start : null,
    observedKwh: null,
    baselineKwh: null,
    ruleVersion: 1,
    flaggedAt: at(end),
    updatedAt: at(end),
  })

  it('keeps flags overlapping the window, newest first', () => {
    const flags = [flag('gap', -60, -15), flag('gap', -15, 30), flag('spike', 45, 60)]

    expect(visibleFlags(flags, from).map((f) => f.id)).toEqual(['spike-45', 'gap--15'])
  })
})
