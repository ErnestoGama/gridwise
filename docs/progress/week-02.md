# Week 02 progress — Identity, buildings and meters

| Day | Task | Status | Evidence |
|---|---|---|---|
| D08 | Acceptance cases and ADR | Not started | |
| D09 | Core data schema and migration | Done | `AddIdentityAndOrganizations` (organizations, users, memberships) and `AddBuildingsAndMeters` (buildings, meters) migrations ([API README](../../services/api/README.md#schema)) |
| D10 | API endpoint and validation | Done | Sign-in endpoints plus `/v1/organizations/{id}/buildings` and `/meters` (list, detail, admin-only create) with `422` field errors, `409` duplicates and cursor pages |
| D11 | UI workflow with error states | Not started | |
| D12 | Unit and integration tests | In progress | `tests/GridWise.Api.Tests`: role ordering, slug rule, login validation, lockout, API sign-in, tenant isolation, building/meter validation, admin-only writes, cross-tenant meter placement and paging |
| D13 | Observability and security checks | In progress | Request IDs on every response and error; sign-in failures logged without the email; hashed passwords and account lockout |
| D14 | Demo, regressions and next risks | Not started | |

## Notes

- Non-members get `404` for an organization, so IDs from other tenants cannot be probed.
- API tests use the EF Core in-memory provider; check constraints and unique
  indexes are only enforced on PostgreSQL and need the integration suite.

- A meter can only reference a building of its own organization: checked by the
  API (`422`) and by the composite foreign key `(organization_id, building_id)`.

## Next

- TimescaleDB hypertable for meter readings, then the telemetry ingestion endpoint.
