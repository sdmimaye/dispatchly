# Outbox messaging library

- Completed: 2026-09-23
- Summary: Publish through an in-memory queue or a PostgreSQL outbox, and deliver at least once.

## Modules

- `Dispatchly.Abstractions`
- `Dispatchly.Core`
- `Dispatchly.DependencyInjection`
- `Dispatchly.DependencyInjection.Reflection`
- `Dispatchly.SourceGenerators`
- `Dispatchly.Transport.InMemory`
- `Dispatchly.Transport.PostgreSql`

## API

- `IMessagePublisher.PublishAsync` takes the message and a `CancellationToken`. A returned `MessageId` is a version-7 GUID. For PostgreSQL it means the row is committed
- `AddHandler`, `AddHandlersFromAssemblies`, and `AddDispatchlyGeneratedHandlers` register handlers. Every handler for a type runs, in registration order
- `AddMessage` registers a type for publishing without a handler. The in-memory transport rejects that publish
- `UseInMemoryTransport` and `UsePostgresTransport` select the transport. A second transport throws
- `UseTableNaming` chooses the table when neither `[DispatchlyTable]` nor a `tableName` argument is set. The default is snake case. Names match `^[A-Za-z_][A-Za-z0-9_]*$` and are at most 51 characters
- `UseBehavior` wraps every delivery. The first registration is the outermost. A return without throwing is acknowledged
- `UseInMemoryIdempotency` and `UsePostgresIdempotency` register `IdempotencyBehavior`. The PostgreSQL inbox is `{schema}.idempotency_inbox`
- `InMemoryTransportOptions.MaxAttempts` defaults to 5 and `RetryDelay` defaults to 20 milliseconds. Exhausted messages stay on `IInMemoryDeadLetterStore` until the process exits
- PostgreSQL defaults: `Schema` `dispatchly`, `VisibilityTimeout` 30 seconds, `MaxAttempts` 5, `MaxBackoff` 5 minutes, `CronSchedule` `* * * * *`, `RedeliveryBatchSize` 100, `ScheduleRedelivery` true. Failure backs off from the visibility timeout, doubling each attempt until `MaxBackoff`
- `PostgresTransportOptions.Channel` is validated at startup. Wake-ups use a private channel per host
- The source generator reports `DLY001` for an invalid table name and `DLY002` for an unsupported message shape
- `IPostgresMaintenance.RedeliverAsync` runs the same function `pg_cron` schedules

## Related

- [Plan](../plan/archive/0001_outbox-messaging.md)
- [Building blocks](../arc42/05_building_block_view.md)
