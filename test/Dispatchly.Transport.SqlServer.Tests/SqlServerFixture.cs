using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Dispatchly.Transport.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Database = "dispatchly";

    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public string MasterConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        MasterConnectionString = _container.GetConnectionString();
        await ExecuteMasterAsync(
            $"""
            IF DB_ID(N'{Database}') IS NULL
                CREATE DATABASE {Quote(Database)};
            """);
        await ExecuteMasterAsync($"ALTER DATABASE {Quote(Database)} SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;");
        ConnectionString = ConnectionStringFor(Database);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task CreateDatabaseAsync(string database)
    {
        await ExecuteMasterAsync(
            $"""
            IF DB_ID({Literal(database)}) IS NULL
                CREATE DATABASE {Quote(database)};
            """);
        await ExecuteMasterAsync($"ALTER DATABASE {Quote(database)} SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE;");
    }

    public string ConnectionStringFor(string database)
    {
        var builder = new SqlConnectionStringBuilder(MasterConnectionString)
        {
            InitialCatalog = database,
            TrustServerCertificate = true,
        };
        return builder.ConnectionString;
    }

    private async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 120,
        };
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string Literal(string value) =>
        "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
