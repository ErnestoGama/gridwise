# Week 02 progress — Identity, buildings and meters

| Day | Task | Status | Evidence |
|---|---|---|---|
| D08 | Acceptance cases and ADR | Not started | |
| D09 | Core data schema and migration | In progress | `AddIdentityAndOrganizations` migration: organizations, users, memberships ([API README](../../services/api/README.md#schema)) |
| D10 | API endpoint and validation | In progress | `/v1/auth/login`, `/v1/auth/logout`, `/v1/me`, `/v1/organizations/{id}` with `422` field errors and the shared error envelope |
| D11 | UI workflow with error states | Not started | |
| D12 | Unit and integration tests | In progress | `tests/GridWise.Api.Tests`: role ordering, slug rule, login validation, lockout, API sign-in and tenant isolation |
| D13 | Observability and security checks | In progress | Request IDs on every response and error; sign-in failures logged without the email; hashed passwords and account lockout |
| D14 | Demo, regressions and next risks | Not started | |

## Notes

- Non-members get `404` for an organization, so IDs from other tenants cannot be probed.
- API tests use the EF Core in-memory provider; check constraints and unique
  indexes are only enforced on PostgreSQL and need the integration suite.

## Next

- Buildings and meters, scoped to an organization and guarded by role.
