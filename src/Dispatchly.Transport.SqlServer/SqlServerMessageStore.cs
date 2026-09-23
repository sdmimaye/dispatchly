using Microsoft.Data.SqlClient;

namespace Dispatchly.Transport.SqlServer;

internal sealed class SqlServerMessageStore
{
    private readonly SqlServerTransportOptions _options;

    public SqlServerMessageStore(SqlServerTransportOptions options) => _options = options;

    public async Task InsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        Guid id,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSql.Insert(_options, table), connection, transaction);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@payload", System.Data.SqlDbType.NVarChar, -1) { Value = payload });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaimResult> ClaimAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        ClaimResult.Ready? ready = null;
        await using (var command = new SqlCommand(SqlServerSql.Claim(_options, table), connection))
        {
            AddClaimParameters(command, id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ready = new ClaimResult.Ready(
                    reader.GetString(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetInt32(2));
            }
        }

        if (ready is not null)
        {
            return ready;
        }

        await using var move = new SqlCommand(SqlServerSql.MoveExhausted(_options, table), connection);
        move.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        move.Parameters.Add(new SqlParameter("@max_attempts", System.Data.SqlDbType.Int) { Value = _options.MaxAttempts });
        await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return ClaimResult.Ignored.Instance;
    }

    public async Task AcknowledgeAsync(string table, Guid id, int attempt, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerSql.Acknowledge(_options, table), connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@attempt", System.Data.SqlDbType.Int) { Value = attempt });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(
        string table,
        Guid id,
        int attempt,
        string error,
        TimeSpan backoff,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerSql.RecordFailure(_options, table), connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@attempt", System.Data.SqlDbType.Int) { Value = attempt });
        command.Parameters.Add(new SqlParameter("@error", System.Data.SqlDbType.NVarChar, -1) { Value = error });
        command.Parameters.Add(new SqlParameter("@backoff_ms", System.Data.SqlDbType.Int)
        {
            Value = SqlServerDelay.Milliseconds(backoff, nameof(backoff)),
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveFailureToDeadLetterAsync(
        string table,
        Guid id,
        int attempt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerSql.MoveFailureToDeadLetter(_options, table), connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@attempt", System.Data.SqlDbType.Int) { Value = attempt });
        command.Parameters.Add(new SqlParameter("@error", System.Data.SqlDbType.NVarChar, -1) { Value = error });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddClaimParameters(SqlCommand command, Guid id)
    {
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@visibility_ms", System.Data.SqlDbType.Int)
        {
            Value = SqlServerDelay.Milliseconds(_options.VisibilityTimeout, nameof(SqlServerTransportOptions.VisibilityTimeout)),
        });
        command.Parameters.Add(new SqlParameter("@max_attempts", System.Data.SqlDbType.Int) { Value = _options.MaxAttempts });
    }
}
