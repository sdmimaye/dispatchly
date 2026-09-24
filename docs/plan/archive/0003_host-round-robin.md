# Host round-robin

- Status: Shipped
- Summary: When several hosts handle the same message type, wake them in round-robin order so each gets an even share of messages. The outbox claim stays the exclusivity mechanism.

## User story

As a service author running more than one host for the same message type, I want each host to receive an even share of wake-ups, so one fast host does not take every message by winning a claim race or a Service Broker receive.

## Acceptance criteria

- The ring for a message table is the live hosts that registered a handler for that table, ordered by consumer id. A publish-only host (`AddMessage`) is not in the ring.
- Each insert and each redelivery advances a per-table cursor by one and wakes `cursor % liveCount`. Fairness is an equal count of messages, not equal handler duration.
- The cursor update is in the same database transaction as the insert, or under a row lock in `redeliver_undelivered`, so concurrent publishers do not hand the same turn to two hosts.
- An empty ring commits the row and notifies nobody. `pg_cron` or SQL Server Agent picks the row up after a host joins.
- Joining or leaving shifts later indexes. An even split is not preserved across membership changes.
- Each handling host inserts a consumer row at start, heartbeats it, and deletes it on graceful stop. A crash leaves the ring after a missed-heartbeat threshold, a transport option that defaults to a few seconds. The consumer id is ephemeral per process start. A restart is a new ring member.
- PostgreSQL: the host `LISTEN`s on its own channel. `notify_outbox` and `redeliver_undelivered` call `pg_notify` on that channel. Channel names match `^[a-z_][a-z0-9_]*$` and are at most 63 characters.
- SQL Server: one queue and target service per consumer, not per table. The trigger and redelivery `SEND` to the chosen consumer’s service. The shared per-table queue is no longer the wake-up target. A stopped host’s queue is dropped with its registration. A crashed host’s queue is dropped when the heartbeat expires.
- The claim `UPDATE` stays. Round-robin only chooses who wakes. A duplicate notify or a redelivery cannot double-apply a row that is locked or already delivered.
- Redelivery uses the same picker and skips hosts whose heartbeat is stale.
- One host: every message is delivered, same as today.
- Two hosts, same handler type: N publishes alternate. Counts differ by at most one.
- A third host joining is included on later messages only.
- Graceful stop removes the host before the next publish.
- Expired heartbeat: the next redelivery wakes a different live host, then that host claims.
- A publish-only host never receives.
- PostgreSQL and SQL Server integration tests use two generic hosts against one Testcontainer database.
- At-least-once delivery and idempotency behavior are unchanged.
- Several `IMessageHandler<T>` registrations in one process still all run for each delivery.

## Non-goals

- In-memory transport
- Choosing among multiple `IMessageHandler<T>` in one process
- Capping in-flight deliveries inside one host
- Exactly-once delivery
- Weighting hosts or balancing by in-flight work

## Open questions

None.

## Related

- [Plan index](0000_readme.md)
- [Building blocks](../arc42/05_building_block_view.md)
