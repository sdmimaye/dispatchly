namespace Dispatchly;

internal static class PostgresSql
{
    public static string Qualify(string schema, string name) =>
        IdentifierRules.Quote(schema) + "." + IdentifierRules.Quote(name);

    public static string NotifyFunction(PostgresTransportOptions options) => $$"""
        CREATE OR REPLACE FUNCTION {{Qualify(options.Schema, "notify_outbox")}}()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $fn$
        BEGIN
            PERFORM pg_notify(TG_ARGV[0], json_build_object('id', NEW.id, 'table', TG_TABLE_NAME)::text);
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
                    PERFORM pg_notify(
                        '{{options.Channel}}',
                        json_build_object('id', rec.id, 'table', tbl)::text);
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
            EXECUTE FUNCTION {Qualify(options.Schema, "notify_outbox")}('{options.Channel}');
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

    public static string Insert(PostgresTransportOptions options, string table) => $"""
        INSERT INTO {Qualify(options.Schema, table)} (id, payload)
        VALUES (@id, @payload);
        """;
}
