// Shapes returned by the API (`services/api`); JSON uses camelCase.

export type Role = 'operator' | 'analyst' | 'manager' | 'admin'

export interface Membership {
  organizationId: string
  organizationName: string
  organizationSlug: string
  role: Role
}

export interface Me {
  id: string
  email: string
  displayName: string
  memberships: Membership[]
}

export interface Page<T> {
  items: T[]
  nextCursor: string | null
}

export interface Building {
  id: string
  name: string
  timeZone: string
  floorAreaM2: number | null
  meterCount: number
  createdAt: string
}

export interface Meter {
  id: string
  buildingId: string
  serial: string
  kind: MeterKind
  intervalMinutes: number
  createdAt: string
}

export interface BuildingDetail {
  id: string
  name: string
  timeZone: string
  floorAreaM2: number | null
  createdAt: string
  updatedAt: string
  meters: Meter[]
}

// Mirrors MeterRules in the API; the API stays the source of truth and
// rejects anything else with a field error.
export const METER_KINDS = ['main', 'hvac', 'lighting', 'plug', 'process'] as const
export type MeterKind = (typeof METER_KINDS)[number]
export const METER_INTERVALS = [5, 10, 15, 30, 60] as const
export const DEFAULT_INTERVAL_MINUTES = 15

/** One stored interval. Same shape in the readings response and in live messages. */
export interface LiveReading {
  sequence: number
  readingStart: string
  energyKwh: number
}

export type QualityFlagKind = 'gap' | 'spike'

export interface QualityFlag {
  id: string
  kind: QualityFlagKind
  intervalStart: string
  intervalEnd: string
  missingIntervals: number | null
  sequence: number | null
  observedKwh: number | null
  baselineKwh: number | null
  ruleVersion: number
  flaggedAt: string
  updatedAt: string
}

export interface MeterWindow {
  meterId: string
  serial: string
  kind: MeterKind
  intervalMinutes: number
  readings: LiveReading[]
  qualityFlags: QualityFlag[]
}

export interface BuildingReadings {
  buildingId: string
  from: string
  to: string
  meters: MeterWindow[]
}

/** Pushed by `/hubs/telemetry` after a batch with new readings is committed. */
export interface ReadingsIngestedMessage {
  organizationId: string
  buildingId: string
  batchId: string
  receivedAt: string
  qualityFlagsChanged: boolean
  meters: { meterId: string; readings: LiveReading[] }[]
}
