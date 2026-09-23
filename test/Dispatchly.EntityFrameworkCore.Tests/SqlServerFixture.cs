using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Dispatchly.EntityFrameworkCore.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Database = "dispatchly";

    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        var master = _container.GetConnectionString();
        await ExecuteAsync(
            master,
            $"""
            IF DB_ID(N'{Database}') IS NULL
                CREATE DATABASE {Quote(Database)};
            """);
        await ExecuteAsync(master, $"ALTER DATABASE {Quote(Database)} SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;");
        var builder = new SqlConnectionStringBuilder(master)
        {
            InitialCatalog = Database,
            TrustServerCertificate = true,
        };
        ConnectionString = builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 120,
        };
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "ef-sqlserver";
}
