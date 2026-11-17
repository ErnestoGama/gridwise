# 0001. Vue/.NET stack and synthetic meter generator strategy

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

GridWise ingests smart-meter readings for buildings, shows them on a live
dashboard, flags abnormal consumption against a baseline, prices consumption
under time-of-use tariffs and compares equipment schedule scenarios. The
signature demo streams synthetic readings, detects an overnight anomaly, shows
the cost breakdown, simulates a schedule change and records an advisory
recommendation.

The demo must run offline on one machine, be repeatable from a clean checkout
and use only synthetic data. GridWise never controls real equipment. Two early
choices shape the rest of the project:

1. How the repository and runtime are laid out, and which component owns which state.
2. Where the meter readings used in development, tests and the demo come from.

## Decision

### Workspace layout

One repository, built as a modular monolith with a separate analytics worker:

| Path | Contents |
|---|---|
| `apps/web/` | Vue 3 + TypeScript application (Vite, Vue Router, Pinia). |
| `services/api/` | ASP.NET Core API (.NET 9) with EF Core and a SignalR hub. The only writer of domain data. |
| `services/worker/` | Python worker for slow jobs: baselines, anomaly scoring, scenario runs, AI explanation drafts. |
| `packages/contracts/` | OpenAPI documents for `/v1`; the TypeScript client is generated from them. |
| `packages/fixtures/` | Synthetic meter data generator and the generated sample sets. |
| `infra/compose/` | Docker Compose stack for local services. |
| `tests/` | Integration and end-to-end tests. |
| `docs/` | ADRs (`docs/adr/`) and weekly progress notes (`docs/progress/`). |

Local services run through Docker Compose with pinned image tags:

| Service | Role |
|---|---|
| PostgreSQL + TimescaleDB | Single source of truth: organizations, buildings, meters, equipment, readings, quality flags, tariffs, cost snapshots, baselines, anomalies, scenarios, alerts and audit events. `meter_readings` is a hypertable partitioned by reading time. Schema changes go through EF Core migrations owned by `services/api`. |
| Redis | Job queue between API and worker, SignalR backplane and short-lived caches. Never authoritative; losing Redis must not lose data. |

Ownership rules:

- **The API owns every write that changes domain state.** The worker stores its
  results (baselines, anomaly candidates, scenario results, AI drafts) as
  versioned records; alerts are raised and acknowledged only through the API.
- **Readings are immutable.** A reading is identified by `(meter_id, sequence)`;
  a unique constraint makes re-sent batches idempotent, so duplicates never
  double-count consumption.
- **Missing data is unknown, not zero.** Gaps are stored as quality flags and
  excluded from totals, never filled with zeros.
- **Money and energy use exact decimals.** Costs use `decimal` in .NET and
  `numeric` in PostgreSQL; tariff windows are evaluated in the building's IANA
  time zone, including daylight-saving transitions.
- **Every query is scoped to an organization** taken from the authenticated
  membership, never from the request body.
- **Recommendations are advisory.** No component sends commands to equipment.
- **The AI adapter is an interface with a deterministic mock.** CI and the demo
  use the mock; a real model is optional configuration. Explanations may only
  cite reading and anomaly IDs that exist in the evidence passed to them.

### Synthetic meter data

- **Generated, not recorded.** A Python generator in `packages/fixtures`
  produces readings from a fixed seed and reference date using only the
  standard library. The same seed always produces byte-identical output, so
  tests can assert exact values.
- **Physically plausible.** Each meter follows a daily load profile (base load,
  occupied-hours load, weekday/weekend difference) plus bounded noise, at a
  fixed interval (15 minutes by default). Energy values are cumulative-free
  interval kWh with three decimal places.
- **Versioned.** Each set carries a `fixture_version`; seeds loaded into the
  database and test snapshots record the version they came from.
- **Plainly fictional.** Organizations and buildings are invented from small
  word lists; addresses and meter serials do not refer to real sites.
- **Edge cases on purpose.** The generator can inject known problems: duplicate
  readings, a telemetry gap, a spike, a negative value, a clock-skewed
  timestamp and an overnight consumption anomaly. Each injected record is listed
  in the fixture manifest so tests know which check must catch it.

## Consequences

- The demo and CI can rebuild everything from nothing with Compose and a seed,
  and failures are reproducible.
- Splitting API and worker adds a queue hop but keeps slow analytics out of
  request handling, and keeps authorization and auditing in one place.
- The .NET build, the Vue build and the Python worker/generator mean three
  toolchains in CI; each gets its own job.
- The generator must change together with the schema; incompatible changes
  bump `fixture_version`.

## Alternatives considered

- **A dedicated time-series database (InfluxDB and similar).** Rejected:
  TimescaleDB keeps readings in the same transactional database as meters,
  tariffs and audit events, with ordinary SQL and foreign keys.
- **Analytics inside the API.** Rejected: baseline and scenario runs are slow
  and better suited to Python's numeric tooling; running them in a worker keeps
  API latency predictable.
- **Microservices per domain area.** Rejected for now: ingestion, costs and
  alerts share one database and one writer. Boundaries are kept as modules so
  they can be split later.
- **Public smart-meter datasets.** Rejected: they cannot produce controlled
  edge cases, and their licences and provenance vary.
