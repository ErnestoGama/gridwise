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

`seed-demo` is idempotent. It creates two fictional organizations and one user
per role, all with the password from `GRIDWISE_DEMO_PASSWORD`:

| User | `harbor-demo` | `lantern-estates` |
|---|---|---|
| `operator@gridwise.test` | operator | — |
| `analyst@gridwise.test` | analyst | — |
| `manager@gridwise.test` | manager | — |
| `admin@gridwise.test` | admin | admin |

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
