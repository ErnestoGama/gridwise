# Week 01 acceptance cases: foundation and synthetic meter data

Scope: the workspace layout, local services and the synthetic meter data
generator described in
[ADR 0001](../adr/0001-vue-dotnet-stack-and-meter-generator.md).
Each case is written as Given / When / Then and must be verifiable from a clean
checkout.

## Workspace and local services

**AC-01.1 — Services start healthy**
- Given Docker is available and `.env` is copied from `.env.example`
- When the Compose stack in `infra/compose` is started
- Then PostgreSQL with the TimescaleDB extension and Redis report healthy, and
  every image tag is pinned to a specific version

**AC-01.2 — Rebuild from nothing**
- Given the stack has been stopped and its volumes removed
- When it is started again
- Then it reaches the same healthy state with no manual steps

**AC-01.3 — Apps build from a clean checkout**
- Given the .NET SDK and Node.js versions named in the repository
- When `services/api` is built and `apps/web` is installed and built
- Then both builds succeed

**AC-01.4 — API health endpoint**
- Given the API is running against the Compose stack
- When `GET /health` is requested
- Then it returns `200` with a body reporting the database and Redis checks, and
  returns `503` when either dependency is unreachable

**AC-01.5 — No secrets committed**
- Given the repository
- When it is searched for credentials
- Then only placeholder values in `.env.example` exist, and `.env` files are
  ignored by git

**AC-01.6 — Services stay local**
- Given the Compose file
- Then service ports are bound to `127.0.0.1` only

## Synthetic meter data generator

**AC-01.7 — Deterministic output**
- Given the default seed and reference date
- When the generator runs twice into two empty directories
- Then both runs produce byte-identical files

**AC-01.8 — Different seed, different data**
- Given two different seeds
- When the generator runs with each
- Then the readings differ, and both sets pass the same validation checks

**AC-01.9 — Regular intervals**
- Given a generated set with the default 15-minute interval
- Then every meter has exactly one clean reading per interval across the
  requested period, aligned to interval boundaries in UTC, except where the
  manifest lists an injected gap or clock skew

**AC-01.10 — Plausible load profile**
- Given a generated set without injected problems
- Then every clean reading is a non-negative kWh value with three decimal
  places, and each meter's mean occupied-hours weekday consumption is higher
  than its mean overnight consumption

**AC-01.11 — Sequences are unique per meter**
- Given a generated set
- Then `(meter_id, sequence)` is unique for every clean reading, and the only
  repeated pairs are the duplicates listed in the manifest

**AC-01.12 — Injected problems are listed**
- Given the generator is asked to inject edge cases
- Then the set contains one of each: duplicate reading, telemetry gap, spike,
  negative value, clock-skewed timestamp and overnight consumption anomaly, and
  the manifest names each by type, meter and sequence

**AC-01.13 — Missing data is not zero**
- Given an injected telemetry gap
- Then the readings in the gap are absent from the set, not written as `0`

**AC-01.14 — Fixture version recorded**
- Given a generated set
- Then its manifest records `fixture_version`, seed, reference date, interval
  and the number of buildings, meters and readings

**AC-01.15 — Plainly fictional**
- Given a generated set
- Then organization and building names come from the generator's word lists,
  meter serials use a `GW-` test prefix, and no real addresses appear

## Risks

| Risk | Mitigation |
|---|---|
| Three toolchains (.NET, Node.js, Python) slow down setup and CI | Pin versions in the repository; one CI job per toolchain |
| Generator drifts from the database schema | Bump `fixture_version` on incompatible changes; ingestion tests load the current set |
| Time-zone and daylight-saving mistakes in profiles and tariffs | Generate in UTC; convert with IANA zones only at the edges; test transition days |
| Fixture data mistaken for real buildings | Word-list names, `GW-` serials, no addresses |
