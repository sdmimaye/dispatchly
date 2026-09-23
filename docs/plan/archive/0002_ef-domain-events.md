# EF Core domain events

- Status: Shipped
- Summary: Collect domain events from aggregates and insert them on the same database transaction as `SaveChanges`. The existing PostgreSQL and SQL Server listeners deliver them after commit.

## User story

As a service author using domain-driven design, I want domain events raised by an aggregate to commit with that aggregate, so a crash between `SaveChanges` and `PublishAsync` cannot lose either the write or the message.

## Acceptance criteria

- `IMessageOutbox.EnlistAsync` inserts on the caller's `DbTransaction`. It does not open a connection, begin a transaction, or commit.
- PostgreSQL requires an `NpgsqlTransaction`. SQL Server requires a `SqlTransaction`. Any other type throws `ArgumentException`. The in-memory transport throws `NotSupportedException`.
- `IDomainEventSource` exposes `DomainEvents` and `ClearDomainEvents`. Each event object is the registered message. There is no mapping to another type.
- `UseDispatchlyOutbox` registers interceptors from the same container as Dispatchly. The context uses the transport's database, and the host is started so the outbox tables exist.
- New events are enlisted on the current transaction. When none is open, the interceptor begins one and commits it after the save. That save runs outside a retrying execution strategy.
- Events stay on the aggregate until the transaction commits. A failed save or a rollback leaves them in place. A caller-owned transaction is not committed by the interceptor.
- Transport tests cover enlist-and-rollback, enlist-and-commit with delivery, and the wrong transaction type. Entity Framework tests cover a successful save, a failed save, and a caller-owned rollback on PostgreSQL and SQL Server.
- EF Core is referenced only by `Dispatchly.EntityFrameworkCore` and `Dispatchly.EntityFrameworkCore.Tests`.

## Non-goals

- Changing `PublishAsync`
- Mapping domain events to other message types
- An in-memory EF path
- A new sample host

## Open questions

None.

## Related

- [Feature record](../../feature/0003_ef-domain-events.md)
- [Building blocks](../../arc42/05_building_block_view.md)
