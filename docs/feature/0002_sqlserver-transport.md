# SQL Server outbox transport

- Completed: 2026-09-23
- Summary: Publish through a SQL Server outbox and wake the host with Service Broker instead of polling.

## Modules

- `Dispatchly.Transport.SqlServer`

## API

- `UseSqlServerTransport` selects the transport
- `PublishAsync` inserts and commits the outbox row before it returns
- An insert trigger `SEND`s `{id, table}` on a Service Broker conversation
- The host blocks in `WAITFOR (RECEIVE)` on one queue per handled message type
- `ISqlServerMaintenance.RedeliverAsync` runs the procedure SQL Server Agent schedules
- Azure SQL Database is unsupported because it has no Service Broker

## Related

- [Building blocks](../arc42/05_building_block_view.md)
