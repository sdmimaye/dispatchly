namespace Dispatchly;

internal static class SqlServerSql
{
    public const int ReceiveTimeoutMilliseconds = 30_000;

    public static string Qualify(string schema, string name) => SqlServerIdentifiers.Qualify(schema, name);

    public static string NotifyProcedure(SqlServerTransportOptions options) => $$"""
        CREATE OR ALTER PROCEDURE {{Qualify(options.Schema, "notify_outbox")}}
            @id uniqueidentifier,
            @table nvarchar(128),
            @initiator nvarchar(256),
            @target nvarchar(256),
            @contract nvarchar(256),
            @message_type nvarchar(256)
        AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @body nvarchar(max) = (
                SELECT @id AS id, @table AS [table]
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            DECLARE @dialog nvarchar(max) = N'
                DECLARE @handle uniqueidentifier;
                BEGIN DIALOG CONVERSATION @handle
                    FROM SERVICE ' + QUOTENAME(@initiator) + N'
                    TO SERVICE ' + QUOTENAME(@target, N'''') + N'
                    ON CONTRACT ' + QUOTENAME(@contract) + N'
                    WITH ENCRYPTION = OFF;
                SEND ON CONVERSATION @handle MESSAGE TYPE ' + QUOTENAME(@message_type) + N' (@body);
                END CONVERSATION @handle;';
            EXEC sys.sp_executesql @dialog, N'@body nvarchar(max)', @body = @body;
        END
        """;

    public static string RedeliverProcedure(SqlServerTransportOptions options)
    {
        var schema = SqlServerIdentifiers.UnicodeLiteral(options.Schema);
        var contract = SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.Contract(options.Schema));
        var messageType = SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.MessageType(options.Schema));
        return $$"""
            CREATE OR ALTER PROCEDURE {{Qualify(options.Schema, "redeliver_undelivered")}}
                @batch_size int
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @batch_size IS NULL OR @batch_size < 1
                    RETURN;

                DECLARE @remaining int = @batch_size;
                DECLARE @last nvarchar(128) = N'';
                DECLARE @table nvarchar(128);
                DECLARE @id uniqueidentifier;
                DECLARE @sql nvarchar(max);
                DECLARE @initiator nvarchar(256);
                DECLARE @target nvarchar(256);
                DECLARE @due TABLE (id uniqueidentifier NOT NULL PRIMARY KEY);

                WHILE @remaining > 0
                BEGIN
                    SET @table = NULL;
                    SELECT TOP (1) @table = table_name
                    FROM {{Qualify(options.Schema, "outbox_table")}}
                    WHERE table_name > @last
                    ORDER BY table_name;
                    IF @table IS NULL
                        BREAK;

                    SET @last = @table;
                    DELETE FROM @due;
                    SET @sql = N'SELECT TOP (@remaining) id FROM {{SqlServerIdentifiers.Quote(options.Schema)}}.' + QUOTENAME(@table)
                        + N' WHERE delivered_at IS NULL AND (locked_until IS NULL OR locked_until <= SYSUTCDATETIME()) ORDER BY created_at';
                    INSERT INTO @due (id)
                    EXEC sys.sp_executesql @sql, N'@remaining int', @remaining = @remaining;

                    WHILE @remaining > 0
                    BEGIN
                        SET @id = NULL;
                        SELECT TOP (1) @id = id FROM @due;
                        IF @id IS NULL
                            BREAK;

                        DELETE FROM @due WHERE id = @id;
                        SET @initiator = {{schema}} + N'/' + @table + N'/initiator';
                        SET @target = {{schema}} + N'/' + @table + N'/target';
                        EXEC {{Qualify(options.Schema, "notify_outbox")}}
                            @id = @id,
                            @table = @table,
                            @initiator = @initiator,
                            @target = @target,
                            @contract = {{contract}},
                            @message_type = {{messageType}};
                        SET @remaining = @remaining - 1;
                    END
                END
            END
            """;
    }

    public static string CreateRegistry(SqlServerTransportOptions options) => $"""
        IF OBJECT_ID(N'{SqlServerIdentifiers.Quote(options.Schema)}.{SqlServerIdentifiers.Quote("outbox_table")}', N'U') IS NULL
            CREATE TABLE {Qualify(options.Schema, "outbox_table")} (
                table_name nvarchar(128) NOT NULL PRIMARY KEY
            );
        """;

    public static string CreateOutbox(SqlServerTransportOptions options, string table)
    {
        var qualified = Qualify(options.Schema, table);
        var index = "ix_" + table + "_due";
        var objectId = "N'" + SqlServerIdentifiers.Quote(options.Schema) + "." + SqlServerIdentifiers.Quote(table) + "'";
        return $"""
            IF OBJECT_ID({objectId}, N'U') IS NULL
                CREATE TABLE {qualified} (
                    id uniqueidentifier NOT NULL PRIMARY KEY,
                    payload nvarchar(max) NOT NULL,
                    created_at datetimeoffset NOT NULL DEFAULT (SYSUTCDATETIME()),
                    attempt_count int NOT NULL DEFAULT (0),
                    locked_until datetimeoffset NULL,
                    delivered_at datetimeoffset NULL,
                    last_error nvarchar(max) NULL
                );
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'{index}' AND object_id = OBJECT_ID({objectId}))
                CREATE INDEX {SqlServerIdentifiers.Quote(index)}
                    ON {qualified} (created_at)
                    WHERE delivered_at IS NULL;
            """;
    }

    public static string CreateDeadLetter(SqlServerTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        var objectId = "N'" + SqlServerIdentifiers.Quote(options.Schema) + "." + SqlServerIdentifiers.Quote(deadLetter) + "'";
        return $"""
            IF OBJECT_ID({objectId}, N'U') IS NULL
                CREATE TABLE {Qualify(options.Schema, deadLetter)} (
                    id uniqueidentifier NOT NULL PRIMARY KEY,
                    payload nvarchar(max) NOT NULL,
                    created_at datetimeoffset NOT NULL,
                    dead_lettered_at datetimeoffset NOT NULL DEFAULT (SYSUTCDATETIME()),
                    attempt_count int NOT NULL,
                    last_error nvarchar(max) NULL
                );
            """;
    }

    public static string EnsureMessageType(SqlServerTransportOptions options)
    {
        var name = SqlServerIdentifiers.MessageType(options.Schema);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.service_message_types WHERE name = {SqlServerIdentifiers.UnicodeLiteral(name)})
                EXEC(N'CREATE MESSAGE TYPE {SqlServerIdentifiers.Quote(name)} VALIDATION = NONE;');
            """;
    }

    public static string EnsureContract(SqlServerTransportOptions options)
    {
        var name = SqlServerIdentifiers.Contract(options.Schema);
        var messageType = SqlServerIdentifiers.MessageType(options.Schema);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.service_contracts WHERE name = {SqlServerIdentifiers.UnicodeLiteral(name)})
                EXEC(N'CREATE CONTRACT {SqlServerIdentifiers.Quote(name)} ({SqlServerIdentifiers.Quote(messageType)} SENT BY INITIATOR);');
            """;
    }

    public static string EnsureQueue(SqlServerTransportOptions options, string table)
    {
        var queue = SqlServerIdentifiers.Queue(table);
        var qualified = Qualify(options.Schema, queue);
        return $"""
            IF NOT EXISTS (
                SELECT 1
                FROM sys.service_queues
                WHERE name = {SqlServerIdentifiers.UnicodeLiteral(queue)}
                  AND schema_id = SCHEMA_ID({SqlServerIdentifiers.UnicodeLiteral(options.Schema)}))
                EXEC(N'CREATE QUEUE {qualified} WITH STATUS = ON, RETENTION = OFF;');
            ELSE
                EXEC(N'ALTER QUEUE {qualified} WITH STATUS = ON, RETENTION = OFF;');
            """;
    }

    public static string EnsureServices(SqlServerTransportOptions options, string table)
    {
        var queue = Qualify(options.Schema, SqlServerIdentifiers.Queue(table));
        var target = SqlServerIdentifiers.TargetService(options.Schema, table);
        var initiator = SqlServerIdentifiers.InitiatorService(options.Schema, table);
        var contract = SqlServerIdentifiers.Contract(options.Schema);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = {SqlServerIdentifiers.UnicodeLiteral(target)})
                EXEC(N'CREATE SERVICE {SqlServerIdentifiers.Quote(target)} ON QUEUE {queue} ({SqlServerIdentifiers.Quote(contract)});');
            IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = {SqlServerIdentifiers.UnicodeLiteral(initiator)})
                EXEC(N'CREATE SERVICE {SqlServerIdentifiers.Quote(initiator)} ON QUEUE {queue};');
            """;
    }

    public static string CreateTrigger(SqlServerTransportOptions options, string table)
    {
        var trigger = Qualify(options.Schema, "trg_" + table + "_notify");
        var notify = Qualify(options.Schema, "notify_outbox");
        return $"""
            CREATE OR ALTER TRIGGER {trigger}
            ON {Qualify(options.Schema, table)}
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @pending TABLE (id uniqueidentifier NOT NULL PRIMARY KEY);
                INSERT INTO @pending (id) SELECT id FROM inserted;
                DECLARE @id uniqueidentifier;
                WHILE EXISTS (SELECT 1 FROM @pending)
                BEGIN
                    SELECT TOP (1) @id = id FROM @pending;
                    DELETE FROM @pending WHERE id = @id;
                    EXEC {notify}
                        @id = @id,
                        @table = {SqlServerIdentifiers.UnicodeLiteral(table)},
                        @initiator = {SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.InitiatorService(options.Schema, table))},
                        @target = {SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.TargetService(options.Schema, table))},
                        @contract = {SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.Contract(options.Schema))},
                        @message_type = {SqlServerIdentifiers.UnicodeLiteral(SqlServerIdentifiers.MessageType(options.Schema))};
                END
            END
            """;
    }

    public static string Claim(SqlServerTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET attempt_count = attempt_count + 1,
            locked_until = DATEADD(millisecond, @visibility_ms, SYSUTCDATETIME())
        OUTPUT inserted.payload, inserted.created_at, inserted.attempt_count
        WHERE id = @id
          AND delivered_at IS NULL
          AND (locked_until IS NULL OR locked_until <= SYSUTCDATETIME())
          AND attempt_count < @max_attempts;
        """;

    public static string MoveExhausted(SqlServerTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        return $"""
            DECLARE @moved TABLE (
                id uniqueidentifier NOT NULL,
                payload nvarchar(max) NOT NULL,
                created_at datetimeoffset NOT NULL,
                attempt_count int NOT NULL,
                last_error nvarchar(max) NULL);
            DELETE FROM {Qualify(options.Schema, table)}
            OUTPUT deleted.id, deleted.payload, deleted.created_at, deleted.attempt_count, deleted.last_error
            INTO @moved (id, payload, created_at, attempt_count, last_error)
            WHERE id = @id
              AND delivered_at IS NULL
              AND (locked_until IS NULL OR locked_until <= SYSUTCDATETIME())
              AND attempt_count >= @max_attempts;
            INSERT INTO {Qualify(options.Schema, deadLetter)}
                (id, payload, created_at, dead_lettered_at, attempt_count, last_error)
            SELECT id, payload, created_at, SYSUTCDATETIME(), attempt_count,
                   COALESCE(last_error, N'max attempts exceeded')
            FROM @moved;
            """;
    }

    public static string Acknowledge(SqlServerTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET delivered_at = SYSUTCDATETIME(),
            locked_until = NULL
        WHERE id = @id
          AND attempt_count = @attempt
          AND delivered_at IS NULL;
        """;

    public static string RecordFailure(SqlServerTransportOptions options, string table) => $"""
        UPDATE {Qualify(options.Schema, table)}
        SET last_error = @error,
            locked_until = DATEADD(millisecond, @backoff_ms, SYSUTCDATETIME())
        WHERE id = @id
          AND attempt_count = @attempt
          AND delivered_at IS NULL;
        """;

    public static string MoveFailureToDeadLetter(SqlServerTransportOptions options, string table)
    {
        var deadLetter = IdentifierRules.DeadLetterTable(table);
        return $"""
            DECLARE @moved TABLE (
                id uniqueidentifier NOT NULL,
                payload nvarchar(max) NOT NULL,
                created_at datetimeoffset NOT NULL,
                attempt_count int NOT NULL);
            DELETE FROM {Qualify(options.Schema, table)}
            OUTPUT deleted.id, deleted.payload, deleted.created_at, deleted.attempt_count
            INTO @moved (id, payload, created_at, attempt_count)
            WHERE id = @id
              AND attempt_count = @attempt
              AND delivered_at IS NULL;
            INSERT INTO {Qualify(options.Schema, deadLetter)}
                (id, payload, created_at, dead_lettered_at, attempt_count, last_error)
            SELECT id, payload, created_at, SYSUTCDATETIME(), attempt_count, @error
            FROM @moved;
            """;
    }

    public static string CreateIdempotencyInbox(SqlServerTransportOptions options)
    {
        var table = IdentifierRules.IdempotencyInboxTable;
        var qualified = Qualify(options.Schema, table);
        var objectId = "N'" + SqlServerIdentifiers.Quote(options.Schema) + "." + SqlServerIdentifiers.Quote(table) + "'";
        return $"""
            IF OBJECT_ID({objectId}, N'U') IS NULL
                CREATE TABLE {qualified} (
                    id uniqueidentifier NOT NULL PRIMARY KEY,
                    completed_at datetimeoffset NOT NULL
                );
            """;
    }

    public static string Insert(SqlServerTransportOptions options, string table) => $"""
        INSERT INTO {Qualify(options.Schema, table)} (id, payload)
        VALUES (@id, @payload);
        """;

    public static string Receive(SqlServerTransportOptions options, string table) => $"""
        WAITFOR (
            RECEIVE TOP (1)
                conversation_handle,
                message_type_name,
                message_body
            FROM {Qualify(options.Schema, SqlServerIdentifiers.Queue(table))}
        ), TIMEOUT {ReceiveTimeoutMilliseconds};
        """;
}
