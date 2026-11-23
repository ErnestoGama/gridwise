import { api } from './client'
import type { BuildingReadings } from './types'

/** Readings and quality flags of every meter of a building in `[from, to)`; the API allows up to 48 hours. */
export function getBuildingReadings(
  organizationId: string,
  buildingId: string,
  from: Date,
  to: Date,
): Promise<BuildingReadings> {
  const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString() })
  return api<BuildingReadings>(
    `/v1/organizations/${encodeURIComponent(organizationId)}/buildings/${encodeURIComponent(buildingId)}/readings?${query}`,
  )
}
