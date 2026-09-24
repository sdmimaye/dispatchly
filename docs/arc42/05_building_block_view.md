# Building block view

```text
Dispatchly.Abstractions
  Dispatchly.Core
    Dispatchly.DependencyInjection
      Dispatchly.DependencyInjection.Reflection
      Dispatchly.SourceGenerators (emits calls into DependencyInjection)
      Dispatchly.Transport.InMemory
      Dispatchly.Transport.PostgreSql
      Dispatchly.Transport.SqlServer
  Dispatchly.EntityFrameworkCore
```

`Dispatchly.Core` owns the message catalog and creates one dependency-injection scope per delivery. Message behaviors registered with `UseBehavior` run inside that scope, outermost first, and then the handlers. Transports publish and acknowledge. They do not reference each other. Registering a second transport throws.

`Dispatchly.EntityFrameworkCore` reads `IDomainEventSource` events during `SaveChanges` and inserts them through `IMessageOutbox` on the current database transaction. It references Abstractions only. Delivery stays on the PostgreSQL or SQL Server transport.

PostgreSQL and SQL Server each keep an internal `outbox_table` registry so `redeliver_undelivered` can visit every message table. Each message type still has its own outbox table and `{table}_dead_letter` table. Handling hosts register in `consumer` and `consumer_table`. Each insert and each redelivery advances `outbox_cursor` and wakes the next live host. PostgreSQL notifies that host's channel. SQL Server sends to that host's queue and target service. A publish-only host is not in the ring.
