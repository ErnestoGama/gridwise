# GridWise API

ASP.NET Core (.NET 9) API with EF Core on PostgreSQL. It is the only writer of
domain data (see [ADR 0001](../../docs/adr/0001-vue-dotnet-stack-and-meter-generator.md)).

## Configuration

| Setting | Example |
|---|---|
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Database=gridwise;Username=gridwise;Password=<from .env>` |
| `ConnectionStrings__Redis` | `localhost:6379` |
| `GRIDWISE_DEMO_PASSWORD` | Only for `seed-demo`; at least 12 characters |

## Commands

```bash
dotnet run -- migrate      # apply EF Core migrations
dotnet run -- seed-demo    # apply migrations, then add the demo organizations and users
dotnet run                 # start the API on http://localhost:5080
dotnet test ../../tests/GridWise.Api.Tests
```

`seed-demo` is idempotent. It creates two fictional organizations, one user
per role (all with the password from `GRIDWISE_DEMO_PASSWORD`) and three
buildings with a `main` and an `hvac` meter each:

| User | `harbor-demo` | `lantern-estates` |
|---|---|---|
| `operator@gridwise.test` | operator | — |
| `analyst@gridwise.test` | analyst | — |
| `manager@gridwise.test` | manager | — |
| `admin@gridwise.test` | admin | admin |

| Organization | Building | Time zone | Meters |
|---|---|---|---|
| `harbor-demo` | Harbor Hall | `Europe/Madrid` | `GW-HH-MAIN-01`, `GW-HH-HVAC-01` |
| `harbor-demo` | Quarry Works | `America/Bogota` | `GW-QW-MAIN-01`, `GW-QW-HVAC-01` |
| `lantern-estates` | Lantern House | `Europe/London` | `GW-LH-MAIN-01`, `GW-LH-HVAC-01` |

## Identity and organizations

Roles are ordered `operator < analyst < manager < admin`; each role can do
everything the lower ones can. A user only sees organizations they are a member
of, and the organization always comes from the route, never from the body.

| Method | Route | Result |
|---|---|---|
| `POST` | `/v1/auth/login` | `{ email, password }` → `200` profile + `gridwise.session` cookie; `401 invalid_credentials`; `422 validation_failed` |
| `POST` | `/v1/auth/logout` | `204`, clears the session |
| `GET` | `/v1/me` | Profile and memberships (`organizationId`, `organizationName`, `organizationSlug`, `role`) |
| `GET` | `/v1/organizations/{id}` | `200` with the caller's role, or `404` if the caller is not a member |
| `GET` | `/health` | Database and Redis status |

Security notes:

- Passwords are hashed with ASP.NET Core Identity's `PasswordHasher` (PBKDF2).
  Unknown emails, wrong passwords and locked accounts get the same `401`.
- Five failed sign-ins in a row lock the account for 15 minutes.
- The session cookie is `HttpOnly`, `SameSite=Strict` and, outside
  development, `Secure`. Unauthenticated API calls get `401` JSON, never a
  redirect.
- Sign-in failures are logged without the email address.

## Buildings and meters

Every member can read the registry; only admins can add buildings and meters.
Routes are relative to `/v1/organizations/{organizationId}`; a caller who is
not a member gets `404` for all of them, a member whose role is too low `403`.

| Method | Route | Result |
|---|---|---|
| `GET` | `/buildings?limit=&cursor=` | Page of buildings in name order, each with `meterCount` |
| `POST` | `/buildings` | `{ name, timeZone, floorAreaM2? }` → `201`; `409 building_name_taken`; `422` |
| `GET` | `/buildings/{id}` | Building with its meters, or `404` |
| `GET` | `/meters?buildingId=&limit=&cursor=` | Page of meters in serial order |
| `POST` | `/meters` | `{ buildingId, serial, kind, intervalMinutes? }` → `201`; `409 meter_serial_taken`; `422` |
| `GET` | `/meters/{id}` | One meter, or `404` |

Rules (each one is also a database constraint):

- Building names are unique within an organization; `timeZone` must be an
  IANA ID such as `Europe/Madrid` (Windows IDs are rejected); floor area is
  optional, 1 to 2,000,000 m².
- Serials are trimmed and uppercased, 3–40 letters, digits or hyphens, and
  unique within an organization (two organizations may reuse a serial).
- `kind` is one of `main`, `hvac`, `lighting`, `plug`, `process`;
  `intervalMinutes` one of 5, 10, 15, 30, 60 (default 15).
- A meter's `buildingId` must belong to the same organization. The API answers
  `422` with `fields.buildingId` otherwise, and the composite foreign key
  `(organization_id, building_id)` blocks it in the database too.
- Pages hold up to `limit` items (1–100, default 50). Pass the opaque
  `nextCursor` back as `cursor`; it is `null` on the last page.

## Telemetry ingestion

`POST /v1/organizations/{organizationId}/telemetry/batches` (admin only) stores
interval readings for any meters of the organization in the route:

```json
{ "readings": [ { "meterId": "…", "sequence": 4812, "readingStart": "2026-03-29T00:15:00Z", "energyKwh": 12.375 } ] }
```

| Result | When |
|---|---|
| `200` `{ batchId, received, inserted, duplicates, conflicts[], receivedAt }` | The batch was valid; new readings were stored and the batch was recorded |
| `200` + `Idempotent-Replayed: true` | Retry with an `Idempotency-Key` already used for the same readings; the original result is returned and nothing is stored |
| `409 idempotency_key_reused` | The `Idempotency-Key` was already used for different readings |
| `422 validation_failed` | Any reading is invalid; `fields` uses keys like `readings[3].energyKwh` (at most 50) and nothing is stored; a malformed key is reported as `idempotencyKey` |
| `404` / `403` / `401` | Not a member / role below admin / not signed in |

Rules:

- 1 to 1,000 readings per batch; the body is limited to 512 KB.
- `meterId` must be a meter of the route organization; another tenant's meter
  is reported as `Meter not found.`
- `readingStart` may carry any offset and is stored in UTC. It must fall on the
  meter's interval boundary, and the interval must have ended (5 minutes of
  clock skew allowed).
- `energyKwh` is 0 to 999,999,999.999 with at most three decimals; `sequence`
  is an integer `>= 0`.
- Readings are immutable and deduplicated by meter and sequence. Resending an
  identical reading counts as a duplicate, so retrying a batch is safe. A
  reading that reuses a stored sequence with another start or energy
  (`sequence_reused`), or takes a stored interval with another sequence
  (`interval_taken`), is listed in `conflicts` with its index and not stored;
  the rest of the batch still is. Within one batch the first reading wins.
- Concurrent batches for the same meter are serialized with a per-meter
  advisory lock inside the insert transaction, because a hypertable cannot
  have a unique `(meter_id, sequence)` constraint without the time column.
- `Idempotency-Key` (optional header) is 1–64 letters, digits or `. _ : -`,
  unique per organization. Readings are compared by a SHA-256 fingerprint of
  the normalized readings, so the same instants with another offset or `12.50`
  instead of `12.5` still count as the same batch.
- Only counts and the batch ID are logged, never reading values or keys.

### Batch history

Every accepted batch is recorded with its counts and conflicts (analyst and
above can read it):

- `GET /v1/organizations/{organizationId}/telemetry/batches?limit=&cursor=` —
  newest first, `{ items: [{ id, received, inserted, duplicates, conflicts,
  hasIdempotencyKey, receivedAt }], nextCursor }`.
- `GET /v1/organizations/{organizationId}/telemetry/batches/{batchId}` — counts,
  `submittedByUserId` and `conflicts[]` (`index`, `meterId`, `sequence`,
  `code`, `message`); `404` for a batch of another organization.

## Errors

Every error uses one envelope; `requestId` matches the `X-Request-ID` response
header (a well-formed incoming `X-Request-ID` is kept):

```json
{ "error": { "code": "validation_failed", "message": "One or more fields are invalid.", "requestId": "…", "fields": { "email": ["Email is required."] } } }
```

## Schema

Migration `AddIdentityAndOrganizations` creates `organizations` (unique
lowercase slug), `users` (unique normalized email, lockout columns) and
`memberships` (one role per user and organization, cascades on delete). Its
`Down` drops the three tables and is only safe before later tables reference
organizations.

Migration `AddBuildingsAndMeters` creates `buildings` (unique name per
organization, alternate key `(organization_id, id)`) and `meters` (unique
serial per organization, kind and interval checks, composite foreign key to
the building). Both use `RESTRICT` on delete so removing an organization or
building never silently removes meter history. `Down` drops both tables and is
only safe before telemetry references meters.

Migration `AddMeterReadingsHypertable` creates `meter_readings` and turns it
into a TimescaleDB hypertable on `reading_start` (7-day chunks):

| Column | Rule |
|---|---|
| `meter_id`, `reading_start` | primary key: one reading per meter and interval; `reading_start` is the UTC interval start, whole minutes |
| `organization_id` | composite foreign key `(organization_id, meter_id)` to `meters`, so a reading cannot belong to another tenant's meter (`RESTRICT` on delete) |
| `sequence` | interval counter from the meter, `>= 0`, indexed with `meter_id` to spot resent readings |
| `energy_kwh` | `numeric(12,3)`, `>= 0` |
| `received_at` | set by the database on insert |

Readings are immutable: a trigger rejects every `UPDATE`. A missing interval has
no row and is treated as unknown, never as zero. The migration needs the
`timescaledb` extension (created by the Compose init script). Its `Down` drops
the table with all stored readings, so dump `meter_readings` before rolling back
a database that holds real data.

Migration `AddTelemetryBatches` creates `telemetry_batches` (counts with a
check that inserted + duplicates + conflicts = received, optional idempotency
key with a partial unique index per organization, SHA-256 payload
fingerprint, submitting user set to null if the user is removed) and
`telemetry_batch_conflicts` (primary key `(batch_id, reading_index)`, code
check, cascades with its batch). `Down` drops both tables; readings are not
touched.
