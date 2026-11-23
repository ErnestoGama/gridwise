# Week 03 progress — Data quality and live dashboard

| Day | Task | Status | Evidence |
|---|---|---|---|
| D22 | Acceptance cases and ADR | Done | [ADR 0002](../adr/0002-telemetry-deduplication-and-quality-flags.md), [acceptance cases](../acceptance/week-03-data-quality-and-live-dashboard.md) |
| D23 | Core data schema and migration | In progress | `AddTelemetryBatches` (`telemetry_batches`, `telemetry_batch_conflicts`, partial unique index on the idempotency key) |
| D24 | API endpoint and validation | In progress | `Idempotency-Key` on `POST …/telemetry/batches` (replay or `409`), `GET …/telemetry/batches` (cursor pages, analyst+) and `GET …/telemetry/batches/{id}` with conflicts |
| D25 | UI workflow with error states | Not started | |
| D26 | Unit and integration tests | In progress | Fingerprint and key rules, batch/conflict model and migration checks, API tests for replay, key reuse, per-organization keys, history paging and access |
| D27 | Observability and security checks | In progress | Batch ID and counts logged; keys, values and serials are not |
| D28 | Demo, regressions and next risks | Not started | |

## Notes

- Deduplication itself (duplicates and conflicts per reading) shipped with
  ingestion in week 2; this week makes its outcome durable and reviewable and
  adds keyed retries.
- Batch history uses UUIDv7 IDs as the keyset, so newest-first paging needs no
  extra timestamp column in the cursor.
- The migration was written by hand and has not been applied to TimescaleDB
  yet; the API code has not been compiled on this host (no .NET SDK).

## Next

- Flag gaps and spikes (`reading_quality_flags`), then the SignalR hub and the
  live dashboard.
