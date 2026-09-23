using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Dispatchly.Transport.SqlServer;

internal sealed class SqlServerIdempotencyStore : IIdempotencyStore
{
    private readonly SqlServerTransportOptions _options;

    public SqlServerIdempotencyStore(SqlServerTransportOptions options) => _options = options;

    public async Task<IIdempotencyLease?> TryLeaseAsync(MessageId id, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        var connectionOwned = true;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var transactionOwned = true;
            try
            {
                await using var command = new SqlCommand(
                    $"""
                    INSERT INTO {SqlServerSql.Qualify(_options.Schema, IdentifierRules.IdempotencyInboxTable)} (id, completed_at)
                    VALUES (@id, SYSUTCDATETIME());
                    """,
                    connection,
                    transaction);
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException exception) when (exception.Number is 2627 or 2601)
                {
                    return null;
                }

                transactionOwned = false;
                connectionOwned = false;
                return new SqlServerIdempotencyLease(connection, transaction);
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

internal sealed class SqlServerIdempotencyLease : IIdempotencyLease
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private bool _committed;

    public SqlServerIdempotencyLease(SqlConnection connection, SqlTransaction transaction)
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
