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

- `IMessagePublisher.PublishAsync` takes the message and a `CancellationToken`
- The PostgreSQL transport commits the outbox row before `PublishAsync` returns
- `AddHandler`, `AddHandlersFromAssemblies`, and `AddDispatchlyGeneratedHandlers` register handlers
- `UseInMemoryTransport` and `UsePostgresTransport` select the transport
- `IPostgresMaintenance.RedeliverAsync` runs the same function `pg_cron` schedules

## Related

- [Plan](../plan/archive/0001_outbox-messaging.md)
- [Building blocks](../arc42/05_building_block_view.md)
