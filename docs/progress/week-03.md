# Week 03 progress — Data quality and live dashboard

| Day | Task | Status | Evidence |
|---|---|---|---|
| D22 | Acceptance cases and ADR | Done | [ADR 0002](../adr/0002-telemetry-deduplication-and-quality-flags.md), [ADR 0003](../adr/0003-realtime-telemetry-updates.md), [acceptance cases](../acceptance/week-03-data-quality-and-live-dashboard.md) |
| D23 | Core data schema and migration | Done | `AddTelemetryBatches` (`telemetry_batches`, `telemetry_batch_conflicts`, partial unique index on the idempotency key), `AddReadingQualityFlags` (`reading_quality_flags`, one flag per meter, start and kind) |
| D24 | API endpoint and validation | Done | `Idempotency-Key` on `POST …/telemetry/batches` (replay or `409`), `GET …/telemetry/batches` (cursor pages, analyst+) and `GET …/telemetry/batches/{id}` with conflicts; `GET …/meters/{id}/quality-flags` (window, kind, cursor; every member); SignalR hub `/hubs/telemetry` with per-building subscriptions and `ReadingsIngested` pushes after commit |
| D25 | UI workflow with error states | Done | Live dashboard at `/organizations/{id}/buildings/{id}/live` (linked from the building page): initial load from `GET …/buildings/{id}/readings` (last 24 h, ≤ 48 h window), SignalR subscription with automatic reconnect and reload after re-subscribing, gaps as chart breaks and unknown counts, gap/spike flag list, connection status (live, reconnecting, offline with reconnect, not available), not-found and retry states |
| D26 | Unit and integration tests | In progress | Fingerprint and key rules, batch/conflict model and migration checks, API tests for replay, key reuse, per-organization keys, history paging and access; gap/spike rule tests and API tests for gaps, late readings, spikes, paging and access; hub tests (no session `401`, live message after ingestion, nothing on resend or after unsubscribing, other organizations refused, subscription cap) and message grouping tests; building readings window, ordering, flags, validation and access tests. 188 tests pass (`dotnet test`, in-memory provider). Web: Vitest tests for merging live readings, gap runs, summaries and chart scaling (9 pass); `vue-tsc` clean and `vite build` passes |
| D27 | Observability and security checks | In progress | Batch ID, counts and flags raised/updated/cleared logged; keys, values and serials are not. Hub: session required, closed on session expiry, 4 KB client messages, refused subscriptions logged with user and organization only |
| D28 | Demo, regressions and next risks | Not started | |

## Notes

- Deduplication itself (duplicates and conflicts per reading) shipped with
  ingestion in week 2; this week makes its outcome durable and reviewable and
  adds keyed retries.
- Batch history uses UUIDv7 IDs as the keyset, so newest-first paging needs no
  extra timestamp column in the cursor.
- Gaps and spikes are recomputed in the ingestion transaction around the new
  readings, so a late reading shrinks or clears its gap right away.
- First full compile and test run on this host: two build errors from week 2
  were fixed on the way (the logout handler's result was discarded, so it
  answered `200` with an empty body instead of `204`; a static field order
  warning in the login service), and the check-constraint tests now read the
  design-time model.
- `dotnet ef migrations has-pending-model-changes` still reports one
  difference from week 2: EF expects an index on `meter_readings
  (organization_id, meter_id)` for the composite foreign key. To be settled
  with the TimescaleDB integration run.
- Migrations have not been applied to TimescaleDB on this host yet.
- Realtime access is checked when subscribing; a single API instance holds the
  groups. Both limits are recorded in ADR 0003 (membership removal must close
  connections; a Redis backplane is needed before running several instances).
- Hub tests run over long polling on the test server; WebSockets through the
  Vite proxy (`/hubs`, `ws: true`) are exercised with the dashboard.
- The dashboard keeps a rolling 24-hour window in memory, adds pushed
  readings to it and reloads the window when flags change, a meter it does not
  know appears, or the connection re-subscribes after a drop.
- `vue-tsc` failed on week 2's time zone list (`Intl.supportedValuesOf`
  needs the ES2022 library); the web tsconfig now includes it.

## Next

- Week 4: time-of-use tariffs and cost breakdown.
- Apply the migrations to TimescaleDB and settle the `meter_readings
  (organization_id, meter_id)` index drift.
- Run the dashboard against the Compose stack end to end (WebSockets through
  the Vite proxy).
