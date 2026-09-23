# Outbox messaging library

- Status: Shipped
- Summary: A .NET outbox library that publishes on the caller's database transaction and delivers at least once through one interchangeable transport. Handlers are registered manually, by assembly scanning, or by a source generator.

## User story

As a service author, I want to commit a message in the same database transaction as my business write, and have a registered handler run after commit, including after a process restart when I use PostgreSQL.

## Acceptance criteria

- `PublishAsync` accepts the caller's `DbTransaction`. The PostgreSQL transport inserts on an `NpgsqlTransaction`, so rollback removes the row.
- Manual registration supports an AOT-safe `JsonTypeInfo<TMessage>` overload and a reflection JSON overload.
- Assembly scanning registers public non-abstract `IMessageHandler<T>` implementations.
- The source generator emits `AddDispatchlyGeneratedHandlers` and a `JsonSerializerContext` with no runtime assembly scan.
- In-memory delivery enqueues inside `PublishAsync`, retries up to `MaxAttempts`, then keeps an in-memory dead letter. A process crash drops the queue.
- PostgreSQL stores each message type in its own outbox table and dead-letter table inside a configurable schema.
- An insert trigger calls `pg_notify`. The .NET host only `LISTEN`s.
- A claim sets a visibility timeout. Success marks `delivered_at` for the same attempt. Failure backs off. Attempts above the maximum move the row to the dead-letter table.
- `redeliver_undelivered` notifies due rows. `ScheduleRedelivery` installs that function on `pg_cron` or fails with an actionable error.
- `dotnet test` covers the dispatcher, registration, generator, in-memory retry, and PostgreSQL commit, rollback, retry, dead letter, visibility, schema, and table layout.

## Non-goals

- EF Core adapter, web UI, Aspire, Identity
- Exactly-once delivery
- Purging delivered rows
- Transports other than in-memory and PostgreSQL

## Open questions

None.

## Related

- [Feature record](../../feature/0001_outbox-messaging.md)
- [Building blocks](../../arc42/05_building_block_view.md)
