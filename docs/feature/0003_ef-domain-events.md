# EF Core domain events

- Completed: 2026-09-23
- Summary: Enlist domain events on the `SaveChanges` transaction so the aggregate and the outbox row commit together.

## Modules

- `Dispatchly.Abstractions`
- `Dispatchly.Transport.PostgreSql`
- `Dispatchly.Transport.SqlServer`
- `Dispatchly.Transport.InMemory`
- `Dispatchly.EntityFrameworkCore`

## API

- `IMessageOutbox.EnlistAsync` inserts on the caller's `DbTransaction` and does not commit it
- PostgreSQL requires `NpgsqlTransaction`; SQL Server requires `SqlTransaction`
- The in-memory transport throws `NotSupportedException`
- `IDomainEventSource` exposes the events an aggregate has raised
- `DomainEventSource` stores those events; `Raise` records one until commit
- `UseDispatchlyOutbox` enlists those events during `SaveChanges` and clears them after commit
- When the save has no transaction, the interceptor begins one and commits it after the save. That save runs outside a retrying execution strategy
- The context uses the same database as the transport, and the host is started so the outbox tables exist
- `PublishAsync` is unchanged

## Sample

- `samples/EntityFrameworkCore` saves an order through PostgreSQL and delivers `OrderPlaced` after commit

## Related

- [Plan](../plan/archive/0002_ef-domain-events.md)
- [Building blocks](../arc42/05_building_block_view.md)
