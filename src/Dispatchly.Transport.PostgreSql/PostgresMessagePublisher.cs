using Npgsql;

namespace Dispatchly;

internal sealed class PostgresMessagePublisher : IMessagePublisher
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly PostgresMessageStore _store;
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMessagePublisher(
        IMessageTypeCatalog catalog,
        PostgresMessageStore store,
        NpgsqlDataSource dataSource)
    {
        _catalog = catalog;
        _store = store;
        _dataSource = dataSource;
    }

    public async ValueTask<MessageId> PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var registration = _catalog.GetRequired(typeof(TMessage));
        var id = MessageId.New();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _store.InsertAsync(
            connection,
            transaction,
            registration.TableName,
            id.Value,
            registration.Serialize(message),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }
}
