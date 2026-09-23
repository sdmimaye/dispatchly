using Npgsql;

namespace Dispatchly.Transport.PostgreSql;

internal sealed class PostgresMaintenance : IPostgresMaintenance
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTransportOptions _options;

    public PostgresMaintenance(NpgsqlDataSource dataSource, PostgresTransportOptions options)
    {
        _dataSource = dataSource;
        _options = options;
    }

    public async Task RedeliverAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            $"SELECT {PostgresSql.Qualify(_options.Schema, "redeliver_undelivered")}(@batch)");
        command.Parameters.AddWithValue("batch", _options.RedeliveryBatchSize);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
