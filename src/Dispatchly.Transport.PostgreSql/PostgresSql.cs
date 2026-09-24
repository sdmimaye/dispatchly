namespace Dispatchly.Transport.PostgreSql;

internal static class PostgresSql
{
    public static string Qualify(string schema, string name) =>
        IdentifierRules.Quote(schema) + "." + IdentifierRules.Quote(name);

    public static string CreateConsumerTables(PostgresTransportOptions options)
    {
        var consumer = Qualify(options.Schema, IdentifierRules.ConsumerTable);
        var membership = Qualify(options.Schema, IdentifierRules.ConsumerMembershipTable);
        var cursor = Qualify(options.Schema, IdentifierRules.OutboxCursorTable);
        return $"""
            CREATE TABLE IF NOT EXISTS {consumer} (
                id text PRIMARY KEY,
                channel text NOT NULL,
                heartbeat_at timestamptz NOT NULL,
                timeout_ms integer NOT NULL
            );
            CREATE TABLE IF NOT EXISTS {membership} (
                consumer_id text NOT NULL REFERENCES {consumer} (id) ON DELETE CASCADE,
                table_name text NOT NULL,
                PRIMARY KEY (consumer_id, table_name)
            );
            CREATE TABLE IF NOT EXISTS {cursor} (
                table_name text PRIMARY KEY,
                cursor bigint NOT NULL
            );
            """;
    }

    public static string WakeFunction(PostgresTransportOptions options)
    {
        var consumer = Qualify(options.Schema, IdentifierRules.ConsumerTable);
        var membership = Qualify(options.Schema, IdentifierRules.ConsumerMembershipTable);
        var cursor = Qualify(options.Schema, IdentifierRules.OutboxCursorTable);
        return $$"""
            CREATE OR REPLACE FUNCTION {{Qualify(options.Schema, "wake_outbox")}}(tbl text, message_id uuid)
            RETURNS void
            LANGUAGE plpgsql
            AS $fn$
            DECLARE
                next_cursor bigint;
                live_count integer;
                chosen text;
            BEGIN
                INSERT INTO {{cursor}} (table_name, cursor)
                VALUES (tbl, 0)
                ON CONFLICT (table_name) DO NOTHING;

                SELECT cursor INTO next_cursor
                FROM {{cursor}}
                WHERE table_name = tbl
                FOR UPDATE;

                DELETE FROM {{consumer}} AS stale
                WHERE stale.heartbeat_at <= clock_timestamp() - (stale.timeout_ms * interval '1 millisecond');

                SELECT COUNT(*) INTO live_count
                FROM {{consumer}} AS live
                INNER JOIN {{membership}} AS live_table ON live_table.consumer_id = live.id
                WHERE live_table.table_name = tbl;

                UPDATE {{cursor}}
                SET cursor = cursor + 1
                WHERE table_name = tbl
                RETURNING cursor INTO next_cursor;

                IF live_count = 0 THEN
                    RETURN;
                END IF;

                SELECT c.channel INTO chosen
                FROM {{consumer}} AS c
                INNER JOIN {{membership}} AS ct ON ct.consumer_id = c.id
                WHERE ct.table_name = tbl
                ORDER BY c.id
                OFFSET (next_cursor % live_count) LIMIT 1;

                IF chosen IS NULL THEN
                    RETURN;
                END IF;

                PERFORM pg_notify(chosen, json_build_object('id', message_id, 'table', tbl)::text);
            END;
            $fn$;
            """;
    }

    public static string NotifyFunction(PostgresTransportOptions options) => $$"""
        CREATE OR REPLACE FUNCTION {{Qualify(options.Schema, "notify_outbox")}}()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $fn$
        BEGIN
            PERFORM {{Qualify(options.Schema, "wake_outbox")}}(TG_TABLE_NAME, NEW.id);
            RETURN NEW;
        END;
        $fn$;
        """;

    public static string RedeliverFunction(PostgresTransportOptions options) => $$"""
        CREATE OR REPLACE FUNCTION {{Qualify(options.Schema, "redeliver_undelivered")}}(batch_size integer)
        RETURNS void
        LANGUAGE plpgsql
        AS $fn$
        DECLARE
            tbl text;
            rec record;
            remaining integer := batch_size;
        BEGIN
            IF remaining IS NULL OR remaining < 1 THEN
                RETURN;
            END IF;

            FOR tbl IN SELECT table_name FROM {{Qualify(options.Schema, "outbox_table")}} LOOP
                EXIT WHEN remaining <= 0;
                FOR rec IN EXECUTE format(
                    'SELECT id FROM %I.%I WHERE delivered_at IS NULL AND (locked_until IS NULL OR locked_until <= clock_timestamp()) ORDER BY created_at LIMIT $1',
                    '{{options.Schema}}',
                    tbl)
                    USING remaining
                LOOP
                    PERFORM {{Qualify(options.Schema, "wake_outbox")}}(tbl, rec.id);
                    remaining := remaining - 1;
                    EXIT WHEN remaining <= 0;
                END LOOP;
            END LOOP;
        END;
        $fn$;
        """;

    public static string CreateOutbox(PostgresTransportOptions options, string table)
    {
        var qualified = Qualify(options.Schema, table);
        return $"""
            CREATE TABLE IF NOT EXISTS {qualified} (
                id uuid PRIMARY KEY,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                attempt_count integer NOT NULL DEFAULT 0,
                locked_until timestamptz NULL,
                delivered_at timestamptz NULL,
                last_error text NULL
            );
            CREATE INDEX IF NOT EXISTS {IdentifierRules.Quote("ix_" + table + "_due")}
                ON {qualified} (created_at)
                WHERE delivered_at IS NULL;
            """;
    }

    public static string CreateDeadLetter(PostgresTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        return $"""
            CREATE TABLE IF NOT EXISTS {Qualify(options.Schema, deadLetter)} (
                id uuid PRIMARY KEY,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                dead_lettered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                attempt_count integer NOT NULL,
                last_error text NULL
            );
            """;
    }

    public static string CreateTrigger(PostgresTransportOptions options, string table)
    {
        var trigger = "trg_" + table + "_notify";
        return $"""
            DROP TRIGGER IF EXISTS {IdentifierRules.Quote(trigger)} ON {Qualify(options.Schema, table)};
            CREATE TRIGGER {IdentifierRules.Quote(trigger)}
            AFTER INSERT ON {Qualify(options.Schema, table)}
            FOR EACH ROW
            EXECUTE FUNCTION {Qualify(options.Schema, "notify_outbox")}();
            """;
    }

    public static string Claim(PostgresTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET attempt_count = attempt_count + 1,
            locked_until = clock_timestamp() + make_interval(secs => @visibility_seconds)
        WHERE id = @id
          AND delivered_at IS NULL
          AND (locked_until IS NULL OR locked_until <= clock_timestamp())
          AND attempt_count < @max_attempts
        RETURNING payload, created_at, attempt_count;
        """;

    public static string MoveExhausted(PostgresTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        return $"""
            WITH moved AS (
                DELETE FROM {Qualify(options.Schema, table)}
                WHERE id = @id
                  AND delivered_at IS NULL
                  AND (locked_until IS NULL OR locked_until <= clock_timestamp())
                  AND attempt_count >= @max_attempts
                RETURNING id, payload, created_at, attempt_count, last_error
            )
            INSERT INTO {Qualify(options.Schema, deadLetter)}
                (id, payload, created_at, dead_lettered_at, attempt_count, last_error)
            SELECT id, payload, created_at, clock_timestamp(), attempt_count,
                   COALESCE(last_error, 'max attempts exceeded')
            FROM moved;
            """;
    }

    public static string Acknowledge(PostgresTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET delivered_at = clock_timestamp(),
            locked_until = NULL
        WHERE id = @id
          AND attempt_count = @attempt
          AND delivered_at IS NULL;
        """;

    public static string RecordFailure(PostgresTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET last_error = @error,
            locked_until = clock_timestamp() + make_interval(secs => @backoff_seconds)
        WHERE id = @id
          AND attempt_count = @attempt
          AND delivered_at IS NULL;
        """;

    public static string MoveFailureToDeadLetter(PostgresTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        return $"""
            WITH moved AS (
                DELETE FROM {Qualify(options.Schema, table)}
                WHERE id = @id
                  AND attempt_count = @attempt
                  AND delivered_at IS NULL
                RETURNING id, payload, created_at, attempt_count
            )
            INSERT INTO {Qualify(options.Schema, deadLetter)}
                (id, payload, created_at, dead_lettered_at, attempt_count, last_error)
            SELECT id, payload, created_at, clock_timestamp(), attempt_count, @error
            FROM moved;
            """;
    }

    public static string CreateIdempotencyInbox(PostgresTransportOptions options) => $"""
        CREATE TABLE IF NOT EXISTS {Qualify(options.Schema, IdentifierRules.IdempotencyInboxTable)} (
            id uuid PRIMARY KEY,
            completed_at timestamptz NOT NULL
        );
        """;

    public static string Insert(PostgresTransportOptions options, string table) => $"""
        INSERT INTO {Qualify(options.Schema, table)} (id, payload)
        VALUES (@id, @payload);
        """;
}
