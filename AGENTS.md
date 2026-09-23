# Dispatchly

Open-source outbox messaging library for .NET. One host registers messages and handlers, then delivers them through a single interchangeable transport.

This is not the modular-monolith application stack. Do not add a web UI, Identity, EF Core, or DispatchR. Do not add Aspire outside `samples/Aspire`.

## Layout

- `src/Dispatchly.Abstractions` — publisher, handler, and message context
- `src/Dispatchly.Core` — catalog, serializer, scoped dispatcher
- `src/Dispatchly.DependencyInjection` — manual Microsoft DI registration
- `src/Dispatchly.DependencyInjection.Reflection` — assembly scanning
- `src/Dispatchly.SourceGenerators` — AOT registration and `JsonSerializerContext`
- `src/Dispatchly.Transport.InMemory` — volatile in-process transport
- `src/Dispatchly.Transport.PostgreSql` — durable transport (`LISTEN` / `NOTIFY`, `pg_cron`)
- `src/Dispatchly.Transport.SqlServer` — durable transport (Service Broker `WAITFOR (RECEIVE)`, SQL Server Agent)
- `test/` — xUnit v3 projects
- `samples/` — runnable registration, transport, and Aspire demonstrations
- `docs/plan/` — specs that have not shipped

## Rules

- Target `net10.0`. Nullable is on. Warnings are errors.
- Public async methods take `CancellationToken` last.
- Assert with xUnit `Assert.*`. Do not add FluentAssertions.
- PostgreSQL integration tests belong in `Dispatchly.Transport.PostgreSql.Tests` and use Testcontainers.
- SQL Server integration tests belong in `Dispatchly.Transport.SqlServer.Tests` and use Testcontainers.
- Only one transport may be registered.
- Handlers must be idempotent. PostgreSQL and SQL Server delivery are at-least-once. In-memory delivery is lost on process exit. `UsePostgresIdempotency` and `UseSqlServerIdempotency` commit handler writes made on the delivery `DbTransaction` together with the message id.
- Do not add `Co-authored-by` trailers to commits.
