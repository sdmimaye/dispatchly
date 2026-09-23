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
```

`Dispatchly.Core` owns the message catalog and creates one dependency-injection scope per delivery. Message behaviors registered with `UseBehavior` run inside that scope, outermost first, and then the handlers. Transports publish and acknowledge. They do not reference each other. Registering a second transport throws.

PostgreSQL and SQL Server each keep an internal `outbox_table` registry so `redeliver_undelivered` can visit every message table. Each message type still has its own outbox table and `{table}_dead_letter` table. SQL Server also creates a Service Broker queue per message table, because one `WAITFOR (RECEIVE)` waits on one queue. A host blocks only on queues for message types it handles.
