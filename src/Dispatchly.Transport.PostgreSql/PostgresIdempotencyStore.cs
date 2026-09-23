using System.Data.Common;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql;

internal sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTransportOptions _options;

    public PostgresIdempotencyStore(NpgsqlDataSource dataSource, PostgresTransportOptions options)
    {
        _dataSource = dataSource;
        _options = options;
    }

    public async Task<IIdempotencyLease?> TryLeaseAsync(MessageId id, CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connectionOwned = true;
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var transactionOwned = true;
            try
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {PostgresSql.Qualify(_options.Schema, IdentifierRules.IdempotencyInboxTable)} (id, completed_at)
                    VALUES (@id, clock_timestamp())
                    ON CONFLICT (id) DO NOTHING
                    RETURNING id;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("id", id.Value);
                var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (inserted is null || inserted is DBNull)
                {
                    return null;
                }

                transactionOwned = false;
                connectionOwned = false;
                return new PostgresIdempotencyLease(connection, transaction);
            }
            finally
            {
                if (transactionOwned)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (connectionOwned)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class PostgresIdempotencyLease : IIdempotencyLease
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    public PostgresIdempotencyLease(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public DbTransaction Transaction => _transaction;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_committed)
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
