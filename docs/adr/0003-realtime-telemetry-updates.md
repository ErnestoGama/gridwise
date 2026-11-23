# 0003. Realtime telemetry updates

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Operators keep a building dashboard open during the day. Polling the API for
every meter every few seconds would load the database for nothing most of the
time and still show readings late. The stack already includes SignalR, and the
API uses cookie sessions, which a SignalR connection can reuse.

A live channel must not become a way around organization access: a user may
only see readings of organizations they belong to, and a building ID from
another organization must not reveal whether it exists.

## Decision

1. **One hub, `/hubs/telemetry`, behind the session cookie.** It has
   `[Authorize]`, so negotiation without a session is `401`, and
   `CloseOnAuthenticationExpiration` closes the connection when the cookie
   expires. The session cookie is `SameSite=Strict`, so another site cannot
   open a connection as the user.
2. **Explicit subscriptions per building.** A connection joins a SignalR group
   only through `SubscribeBuilding(organizationId, buildingId)`, which checks
   membership (any role) and that the building belongs to that organization.
   Both failures answer `not_found`. Group names hold both IDs. A connection
   can follow at most 20 buildings.
3. **Push after commit only.** The ingestion endpoint publishes once the
   transaction has committed: one message per building with the newly stored
   readings, grouped by meter, and whether quality flags changed. Duplicates,
   conflicts and replays publish nothing. Flags are not repeated in the
   message; the client reloads them from the API.
4. **Best effort.** A failed push is logged (batch and building IDs only) and
   the batch still succeeds. The dashboard loads its data from the API on open
   and on reconnect, so a missed message never leaves wrong data on screen for
   longer than a reconnect.

## Consequences

- Readings reach open dashboards right after they are stored, with no polling.
- Access is checked when subscribing, not on every message. A membership
  removed while a connection is open keeps receiving updates until the
  connection closes or the session expires. There is no membership removal
  yet; when it is added, it must also close that user's connections.
- Groups live in one API process. Running more than one instance needs a
  SignalR backplane (Redis is already in the stack) before scaling out.
- Messages carry reading values, so they are only sent to the building group,
  never broadcast, and are not logged.
