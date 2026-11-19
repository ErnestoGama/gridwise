# Week 02 progress — Identity, buildings and meters

| Day | Task | Status | Evidence |
|---|---|---|---|
| D08 | Acceptance cases and ADR | Not started | |
| D09 | Core data schema and migration | Done | `AddIdentityAndOrganizations` (organizations, users, memberships) and `AddBuildingsAndMeters` (buildings, meters) and `AddMeterReadingsHypertable` (TimescaleDB `meter_readings`) migrations ([API README](../../services/api/README.md#schema)) |
| D10 | API endpoint and validation | Done | Sign-in endpoints plus `/v1/organizations/{id}/buildings` and `/meters` (list, detail, admin-only create) with `422` field errors, `409` duplicates and cursor pages; `POST /v1/organizations/{id}/telemetry/batches` with per-reading `422` errors, duplicate and conflict counts |
| D11 | UI workflow with error states | Done | `apps/web`: sign-in, buildings list (load more, admin-only add form) and building detail with meters (admin-only register form); loading, empty, 404, 403, field-level 422 and 409 messages; an expired session returns to sign-in |
| D12 | Unit and integration tests | In progress | `tests/GridWise.Api.Tests`: role ordering, slug rule, login validation, lockout, API sign-in, tenant isolation, building/meter validation, admin-only writes, cross-tenant meter placement and paging, reading energy/alignment rules, reading key and foreign key, hypertable migration order, batch validation and deduplication, idempotent resends, cross-tenant meters in a batch |
| D13 | Observability and security checks | In progress | Request IDs on every response and error; sign-in failures logged without the email; hashed passwords and account lockout; telemetry batches bounded (1,000 readings, 512 KB) and logged as counts only |
| D14 | Demo, regressions and next risks | Not started | |

## Notes

- Non-members get `404` for an organization, so IDs from other tenants cannot be probed.
- API tests use the EF Core in-memory provider; check constraints and unique
  indexes are only enforced on PostgreSQL and need the integration suite.

- A meter can only reference a building of its own organization: checked by the
  API (`422`) and by the composite foreign key `(organization_id, building_id)`.

- Readings live in a TimescaleDB hypertable keyed by `(meter_id, reading_start)`;
  Timescale needs the time column in every unique index, so the `(meter_id,
  sequence)` duplicate check from the fixtures is done by ingestion, with an
  index to support it. An `UPDATE` trigger keeps readings immutable.
- The migration was written by hand and has not been applied to TimescaleDB yet.
- Ingestion is scoped by the route organization like the other endpoints
  (`/v1/organizations/{id}/telemetry/batches`). Identical resends are counted
  as duplicates; changed values for a stored sequence or interval are reported
  as conflicts and not stored, since readings are immutable. A per-meter
  advisory lock covers the duplicate check, which no unique index can enforce
  on the hypertable. The in-memory API tests do not exercise the lock; that
  needs the TimescaleDB integration suite.

- The web app only shows the add forms to admins, but the API still enforces
  the role; a `403` is shown as a message, not hidden.
- The meter kind and interval lists in the web app mirror `MeterRules`; the API
  remains the source of truth and rejects other values with field errors.
- The web app has not been built yet (no Node toolchain on the build host), so
  `npm run typecheck` still needs a first run.

## Next

- Week 3: deduplicate readings, flag gaps and spikes, then the live dashboard.
