# Introduction and goals

Dispatchly delivers messages to in-process handlers with at-least-once semantics.

Every transport publishes through `IMessagePublisher`. PostgreSQL and SQL Server insert and commit the row before `PublishAsync` returns. PostgreSQL then wakes a hosted service with `LISTEN` / `NOTIFY`, and `pg_cron` re-notifies anything that was not acknowledged. SQL Server does the same with Service Broker `SEND` and `WAITFOR (RECEIVE)`, and SQL Server Agent runs redelivery. The in-memory transport drops messages when the process exits.

Handlers are idempotent. A crash after the handler returns and before the acknowledgement can deliver the same message again. Message behaviors wrap that delivery. `UsePostgresIdempotency` and `UseSqlServerIdempotency` commit handler writes made on the delivery transaction together with the message id, so that crash skips the handler on the next attempt.

`Dispatchly.EntityFrameworkCore` enlists those same messages from `IDomainEventSource` on the `SaveChanges` transaction, so the aggregate and the outbox row commit together. `PublishAsync` still commits on its own connection.
