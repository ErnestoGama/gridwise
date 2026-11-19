import { api, postJson } from './client'
import type { Building, BuildingDetail, Me, Meter, Page } from './types'

const org = (organizationId: string) => `/v1/organizations/${encodeURIComponent(organizationId)}`

export const getMe = () => api<Me>('/v1/me')

export const login = (email: string, password: string) => postJson<Me>('/v1/auth/login', { email, password })

export const logout = () => api<void>('/v1/auth/logout', { method: 'POST' })

export function listBuildings(organizationId: string, cursor?: string | null): Promise<Page<Building>> {
  const query = new URLSearchParams({ limit: '25' })
  if (cursor) {
    query.set('cursor', cursor)
  }
  return api<Page<Building>>(`${org(organizationId)}/buildings?${query}`)
}

export interface NewBuilding {
  name: string
  timeZone: string
  floorAreaM2: number | null
}

export const createBuilding = (organizationId: string, building: NewBuilding) =>
  postJson<Building>(`${org(organizationId)}/buildings`, building)

export const getBuilding = (organizationId: string, buildingId: string) =>
  api<BuildingDetail>(`${org(organizationId)}/buildings/${encodeURIComponent(buildingId)}`)

export interface NewMeter {
  buildingId: string
  serial: string
  kind: string
  intervalMinutes: number
}

export const createMeter = (organizationId: string, meter: NewMeter) =>
  postJson<Meter>(`${org(organizationId)}/meters`, meter)
