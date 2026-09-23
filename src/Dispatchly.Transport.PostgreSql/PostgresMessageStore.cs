using Npgsql;
using NpgsqlTypes;

namespace Dispatchly.Transport.PostgreSql;

internal sealed class PostgresMessageStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTransportOptions _options;

    public PostgresMessageStore(NpgsqlDataSource dataSource, PostgresTransportOptions options)
    {
        _dataSource = dataSource;
        _options = options;
    }

    public async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        Guid id,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PostgresSql.Insert(_options, table), connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaimResult> ClaimAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ClaimResult.Ready? ready = null;
        await using (var command = new NpgsqlCommand(PostgresSql.Claim(_options, table), connection))
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

        await using var move = new NpgsqlCommand(PostgresSql.MoveExhausted(_options, table), connection);
        AddClaimParameters(move, id);
        await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return ClaimResult.Ignored.Instance;
    }

    public async Task AcknowledgeAsync(string table, Guid id, int attempt, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresSql.Acknowledge(_options, table), connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
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
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresSql.RecordFailure(_options, table), connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("backoff_seconds", backoff.TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveFailureToDeadLetterAsync(
        string table,
        Guid id,
        int attempt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresSql.MoveFailureToDeadLetter(_options, table), connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddClaimParameters(NpgsqlCommand command, Guid id)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("visibility_seconds", _options.VisibilityTimeout.TotalSeconds);
        command.Parameters.AddWithValue("max_attempts", _options.MaxAttempts);
    }
}
