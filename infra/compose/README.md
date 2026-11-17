# Local services

PostgreSQL with TimescaleDB and Redis for development. Ports are bound to
`127.0.0.1` only.

```sh
cd infra/compose
cp .env.example .env          # then replace the placeholder password
docker compose up -d --wait   # waits until both services are healthy
```

To start from nothing again, stop the stack with `docker compose down --volumes`
(this deletes the database volume) and run `docker compose up -d --wait`.

## Running the API against the stack

The API reads its connection strings from configuration; none are committed.
Set them as environment variables or with `dotnet user-secrets`:

```sh
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=gridwise;Username=gridwise;Password=<your password>"
export ConnectionStrings__Redis="127.0.0.1:6379"
dotnet run --project services/api --launch-profile http
curl -i http://localhost:5080/health
```

`/health` returns `200` when both checks pass and `503` when either dependency
is unreachable.

## Web app

```sh
cd apps/web
npm install
npm run dev      # http://localhost:5173, proxies /health and /v1 to the API
npm run build    # type-checks, then builds into dist/
```
