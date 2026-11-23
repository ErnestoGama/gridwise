import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { onBeforeUnmount, ref, watch, type Ref } from 'vue'

import type { ReadingsIngestedMessage } from '@/api/types'

/**
 * - `connecting`: first connection or subscription in progress
 * - `live`: subscribed; new readings arrive as they are stored
 * - `reconnecting`: connection lost, retrying; nothing arrives meanwhile
 * - `offline`: retries gave up or the server closed the connection
 *   (e.g. the session ended); a manual retry is offered
 * - `denied`: the API refused the subscription (`not_found`)
 */
export type FeedStatus = 'connecting' | 'live' | 'reconnecting' | 'offline' | 'denied'

export interface BuildingFeedOptions {
  /** Called for each `ReadingsIngested` message of the followed building. */
  onReadings: (message: ReadingsIngestedMessage) => void
  /**
   * Called after a reconnect re-subscribed. Messages sent while the
   * connection was down are lost, so the caller reloads its data.
   */
  onResubscribed: () => void
}

const HUB_PATH = '/hubs/telemetry'
// Retry quickly at first, then every 30 s; give up after about two minutes.
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000, 30_000, 30_000]

/**
 * Follows one building on the telemetry hub. The session cookie authenticates
 * the connection; the hub checks membership on every subscription. Changing
 * the building moves the subscription, and leaving the page closes the
 * connection.
 */
export function useBuildingFeed(
  organizationId: Ref<string>,
  buildingId: Ref<string>,
  options: BuildingFeedOptions,
) {
  const status = ref<FeedStatus>('connecting')
  const lastMessageAt = ref<Date | null>(null)

  let connection: HubConnection | null = null
  let subscribed: { organizationId: string; buildingId: string } | null = null

  function build(): HubConnection {
    const hub = new HubConnectionBuilder()
      .withUrl(HUB_PATH, {
        // WebSockets first; long polling keeps working behind proxies that block upgrades.
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
        withCredentials: true,
      })
      .withAutomaticReconnect(RETRY_DELAYS_MS)
      .configureLogging(LogLevel.Warning)
      .build()

    hub.on('ReadingsIngested', (message: ReadingsIngestedMessage) => {
      if (
        subscribed &&
        message.organizationId === subscribed.organizationId &&
        message.buildingId === subscribed.buildingId
      ) {
        lastMessageAt.value = new Date()
        options.onReadings(message)
      }
    })
    hub.onreconnecting(() => {
      status.value = 'reconnecting'
    })
    hub.onreconnected(async () => {
      // A reconnect is a new connection ID: its groups are gone.
      const target = subscribed
      subscribed = null
      if (target) {
        await subscribe(target.organizationId, target.buildingId)
        if (status.value === 'live') {
          options.onResubscribed()
        }
      }
    })
    hub.onclose(() => {
      subscribed = null
      if (status.value !== 'denied') {
        status.value = 'offline'
      }
    })
    return hub
  }

  async function subscribe(org: string, building: string): Promise<void> {
    if (!connection || connection.state !== HubConnectionState.Connected) {
      return
    }
    status.value = 'connecting'
    try {
      await connection.invoke('SubscribeBuilding', org, building)
      subscribed = { organizationId: org, buildingId: building }
      status.value = 'live'
    } catch (error) {
      subscribed = null
      status.value = error instanceof Error && error.message.includes('not_found') ? 'denied' : 'offline'
    }
  }

  async function unsubscribe(): Promise<void> {
    const current = subscribed
    subscribed = null
    if (current && connection?.state === HubConnectionState.Connected) {
      try {
        await connection.invoke('UnsubscribeBuilding', current.organizationId, current.buildingId)
      } catch {
        // The connection is going away or the server dropped the group already.
      }
    }
  }

  async function start(): Promise<void> {
    status.value = 'connecting'
    if (!connection) {
      connection = build()
    }
    if (connection.state === HubConnectionState.Disconnected) {
      try {
        await connection.start()
      } catch {
        status.value = 'offline'
        return
      }
    }
    await subscribe(organizationId.value, buildingId.value)
  }

  async function stop(): Promise<void> {
    const current = connection
    connection = null
    subscribed = null
    if (current) {
      await current.stop()
    }
  }

  /** Manual retry after `offline`; the caller reloads its data on success. */
  async function retry(): Promise<boolean> {
    await start()
    return status.value === 'live'
  }

  watch([organizationId, buildingId], async () => {
    await unsubscribe()
    await start()
  })

  void start()
  onBeforeUnmount(() => {
    void stop()
  })

  return { status, lastMessageAt, retry }
}
