using Microsoft.Data.SqlClient;

namespace Dispatchly.Transport.SqlServer;

internal sealed class SqlServerMaintenance : ISqlServerMaintenance
{
    private readonly SqlServerTransportOptions _options;

    public SqlServerMaintenance(SqlServerTransportOptions options) => _options = options;

    public async Task RedeliverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"EXEC {SqlServerSql.Qualify(_options.Schema, "redeliver_undelivered")} @batch_size = @batch",
            connection);
        command.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.Int) { Value = _options.RedeliveryBatchSize });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
