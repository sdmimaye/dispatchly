# Dispatchly

Status: initial outbox transports shipped (in-memory, PostgreSQL, and SQL Server), plus EF Core domain-event enlistment.

Dispatchly is an outbox messaging library for .NET. Enqueue a message through `IMessagePublisher`, then deliver it to an in-process handler at least once.

PostgreSQL and SQL Server are the durable transports. The in-memory transport keeps messages only until the process exits.

## Packages

| Project | Role |
| --- | --- |
| `Dispatchly.Abstractions` | Handler and publisher contracts |
| `Dispatchly.Core` | Message catalog and scoped dispatcher |
| `Dispatchly.DependencyInjection` | Manual Microsoft DI registration |
| `Dispatchly.DependencyInjection.Reflection` | Assembly scanning. Not an AOT path |
| `Dispatchly.SourceGenerators` | Compile-time registration and `JsonSerializerContext` |
| `Dispatchly.Transport.InMemory` | Volatile in-process queue |
| `Dispatchly.Transport.PostgreSql` | Durable outbox using `LISTEN` / `NOTIFY` |
| `Dispatchly.Transport.SqlServer` | Durable outbox using Service Broker `WAITFOR (RECEIVE)` |
| `Dispatchly.EntityFrameworkCore` | Enlists `IDomainEventSource` events on the `SaveChanges` transaction |

Register one transport. A second `Use*Transport` call throws.

## Durability

Handlers must be idempotent. A crash after the handler returns and before the row is marked delivered can run the handler again. `UsePostgresIdempotency` and `UseSqlServerIdempotency` close that gap when the handler commits its writes on the delivery transaction. `UseInMemoryIdempotency` only remembers identifiers until the process exits.

The PostgreSQL transport inserts and commits the row inside `IMessagePublisher.PublishAsync`. A returned `MessageId` means the row is durable. `pg_notify` runs from an insert trigger and is delivered only after that commit. The .NET host only `LISTEN`s. It does not poll. Publish is not enlisted in any other database transaction.

`pg_cron` runs `redeliver_undelivered`, which notifies rows whose visibility lock has expired. Set `PostgresTransportOptions.ScheduleRedelivery` to false only when the database does not have `pg_cron`, and call `IPostgresMaintenance.RedeliverAsync` from your own scheduler. If scheduling is left on and the extension is missing, startup throws.

The SQL Server transport uses the same outbox tables and claim rules. The insert trigger `SEND`s a Service Broker message after the commit, and the host blocks in `WAITFOR (RECEIVE)`. It does not poll. Each handled message type has its own queue, so a publish-only registration does not consume another host's wake-up. Messages stay in the queue until received, including messages published while the process is down. SQL Server Agent runs `redeliver_undelivered` for rows whose visibility lock has expired. Set `SqlServerTransportOptions.ScheduleRedelivery` to false when Agent is not running, and call `ISqlServerMaintenance.RedeliverAsync` from your own scheduler. If scheduling is left on and Agent is stopped, startup throws. Azure SQL Database has no Service Broker, so this transport cannot run there.

The in-memory transport enqueues inside `IMessagePublisher.PublishAsync`. Anything still queued is lost when the process exits, including messages whose handlers have not finished.

## Domain events

An aggregate extends `Dispatchly.Abstractions.DomainEventSource`, or implements `IDomainEventSource` when it already has a base class. Each event object is a registered message. There is no mapping to a different type. `Dispatchly.EntityFrameworkCore.UseDispatchlyOutbox` enlists those events on the `SaveChanges` transaction through `IMessageOutbox`. The interceptor clears them after that transaction commits. A failed save or a rollback leaves them on the aggregate.

The context uses the same database as the transport, and the host is started so the outbox tables and triggers exist. When the save has no transaction, the interceptor begins one and commits it after the save. That save runs outside a retrying execution strategy. The in-memory transport cannot enlist on a database transaction.

```csharp
using Dispatchly.Abstractions;
using Dispatchly.EntityFrameworkCore;

public sealed class Order : DomainEventSource
{
    public string Number { get; set; } = "";

    public void Place() => Raise(new OrderPlaced(Number));
}

services.AddDbContext<OrdersDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.UseDispatchlyOutbox(sp);
});
```

## Registration

Manual AOT-safe registration takes a `JsonTypeInfo<TMessage>`:

```csharp
services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>(OrderJsonContext.Default.OrderPlaced)
    .UsePostgresTransport(options =>
    {
        options.ConnectionString = connectionString;
    });

SQL Server registration is the same shape:

```csharp
services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>(OrderJsonContext.Default.OrderPlaced)
    .UseSqlServerTransport(options =>
    {
        options.ConnectionString = connectionString;
    });
```
```

The reflection overload `AddHandler<TMessage, THandler>()` and `AddHandlersFromAssemblies` use reflection-based JSON and honor `[DispatchlyTable("table_name")]`. They are not trimming or AOT compatible.

`AddMessage<TMessage>(jsonTypeInfo)` registers a type for publishing without a handler. A PostgreSQL listener in that process does not claim it, and a SQL Server host does not receive on that type's queue, so another host can deliver it. The in-memory transport rejects that publish, because it can only deliver inside the current process.

The source generator finds public `IMessageHandler<T>` implementations and emits `AddDispatchlyGeneratedHandlers` plus a `JsonSerializerContext`. Reference `Dispatchly.SourceGenerators` as an analyzer, then:

```csharp
services.AddDispatchly()
    .AddDispatchlyGeneratedHandlers()
    .UseInMemoryTransport();
```

Generated serialization supports public properties of primitive types, `string`, `Guid`, and the built-in date and time types. For anything else, pass your own `JsonTypeInfo<T>`.

## Pipeline

`UseBehavior<TBehavior>` adds a step around every delivery. The first registration is the outermost. A behavior sees the deserialized message, `MessageContext`, and the delivery scope. Call `next` to continue. Return without calling it to stop the pipeline. A delivery that returns without throwing is still acknowledged.

```csharp
services.AddDispatchly()
    .AddHandler<OrderPlaced, OrderPlacedHandler>(OrderJsonContext.Default.OrderPlaced)
    .UsePostgresTransport(options => options.ConnectionString = connectionString)
    .UsePostgresIdempotency();
```

`UsePostgresIdempotency`, `UseSqlServerIdempotency`, and `UseInMemoryIdempotency` register `IdempotencyBehavior`. Call the transport method first. The durable stores create `{schema}.idempotency_inbox`. That name is reserved and cannot be a message table.

The behavior inserts the message id inside a database transaction and commits that transaction only after the handler returns. Put handler writes on `context.GetRequiredFeature<DbTransaction>()` so they commit or roll back with the inbox row. A crash before the commit rolls the writes back and delivery runs again. A crash after the commit, including a crash before the outbox row is acknowledged, skips the handler. The in-memory store has no transaction. Inbox rows are kept. Purging them is out of scope.

A different store can implement `IIdempotencyStore` and be registered with `UseBehavior<IdempotencyBehavior>()`.

## Outbox layout

Each message type gets `{schema}.{table}` and `{schema}.{table}_dead_letter`. The default schema is `dispatchly`. Table names are snake_case unless `[DispatchlyTable]` or the `tableName` argument says otherwise. SQL Server also creates `{schema}.{table}_queue` and a Service Broker service pair for that table.

A claim increments `attempt_count` and sets `locked_until`. Success sets `delivered_at` only when the attempt still matches. Failure stores `last_error` and backs off. When the attempt reaches `MaxAttempts`, the row moves to that type's dead-letter table. Delivered rows are kept. Purging them is out of scope.

## PostgreSQL publish

```csharp
var id = await host.Services.GetRequiredService<IMessagePublisher>()
    .PublishAsync(new OrderPlaced("1001"));
```

`PostgresTransportOptions.ConnectionString` is the database used for the insert, the listener, and schema provisioning. Start the generic host before publishing so the schema exists and `LISTEN` is active. Messages published while the process is down are picked up by `pg_cron`.

## SQL Server publish

`SqlServerTransportOptions.ConnectionString` must target a user database with Service Broker enabled (`ALTER DATABASE [name] SET ENABLE_BROKER`). Start the generic host before publishing so the schema, queues, and `WAITFOR (RECEIVE)` loops exist. Each handled message type holds one receive connection. Messages published while the process is down remain on the queue and are delivered when a handler host starts. Rows left invisible by a crash are picked up by SQL Server Agent, or by `ISqlServerMaintenance.RedeliverAsync` when Agent is not scheduled.

## Build

```bash
dotnet test
```

PostgreSQL and SQL Server tests use Testcontainers and need Docker. The SQL Server image is `mcr.microsoft.com/mssql/server:2022-latest`.

## Samples

| Project | What it shows |
| --- | --- |
| `samples/Registration.Manual` | `AddHandler` with `JsonTypeInfo` |
| `samples/Registration.Reflection` | Reflection `AddHandler`, including `[DispatchlyTable]` |
| `samples/Registration.Scanning` | `AddHandlersFromAssemblies` |
| `samples/Registration.SourceGenerator` | `AddDispatchlyGeneratedHandlers` |
| `samples/Transport.InMemory` | In-memory transport |
| `samples/Transport.PostgreSql` | PostgreSQL transport through `IMessagePublisher` |
| `samples/Transport.SqlServer` | SQL Server transport through `IMessagePublisher` |
| `samples/EntityFrameworkCore` | Domain events enlisted on the `SaveChanges` transaction |
| `samples/Aspire` | Checkout publishes and Shipping handles through one PostgreSQL database |

Registration and in-memory samples:

```bash
dotnet run --project samples/Registration.Manual
dotnet run --project samples/Transport.InMemory
```

PostgreSQL sample. `ScheduleRedelivery` is off, so the database does not need `pg_cron`. The same process listens and delivers.

```bash
DISPATCHLY_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=dispatchly" \
  dotnet run --project samples/Transport.PostgreSql
```

SQL Server sample. `ScheduleRedelivery` is off, so SQL Server Agent is not required. The database must already have Service Broker enabled. The same process receives and delivers.

```bash
DISPATCHLY_SQLSERVER="Server=localhost;Database=dispatchly;User Id=sa;Password=your-password;TrustServerCertificate=True" \
  dotnet run --project samples/Transport.SqlServer
```

Entity Framework sample. The host must be able to create the `orders` table. `ScheduleRedelivery` is off, so the database does not need `pg_cron`. `SaveChanges` commits the order and `OrderPlaced` together, and this process delivers the event.

```bash
DISPATCHLY_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=dispatchly" \
  dotnet run --project samples/EntityFrameworkCore
```

Aspire sample. Docker is required. Checkout calls `AddMessage` and Shipping calls `AddHandler` for the same `OrderPlaced` contract. The stock PostgreSQL container has no `pg_cron`, so both apps leave redelivery scheduling off. Shipping calls `IPostgresMaintenance.RedeliverAsync` once after it starts listening.

```bash
dotnet run --project samples/Aspire/AppHost
```
