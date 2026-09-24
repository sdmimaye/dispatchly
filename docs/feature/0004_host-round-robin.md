# Host round-robin

- Completed: 2026-09-24
- Summary: When several hosts handle the same message type, wake them in round-robin order. The outbox claim stays the exclusivity mechanism.

## Modules

- `Dispatchly.Transport.PostgreSql`
- `Dispatchly.Transport.SqlServer`

## API

- `PostgresTransportOptions.HeartbeatTimeout` and `SqlServerTransportOptions.HeartbeatTimeout` default to 5 seconds
- A handling host inserts a `consumer` row at start, heartbeats it, and deletes it on graceful stop
- Each insert and each redelivery advances `outbox_cursor` and wakes `cursor % liveCount`
- PostgreSQL `LISTEN`s on a per-host channel. `notify_outbox` and `redeliver_undelivered` call `pg_notify` on that channel
- SQL Server uses one queue and target service per consumer. The trigger and redelivery `SEND` to that service
- A publish-only host (`AddMessage`) is not in the ring
- An empty ring commits the row and notifies nobody

## Related

- [Plan](../plan/archive/0003_host-round-robin.md)
- [Building blocks](../arc42/05_building_block_view.md)
