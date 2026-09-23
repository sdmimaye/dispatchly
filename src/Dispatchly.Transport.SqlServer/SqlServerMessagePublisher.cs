using Microsoft.Data.SqlClient;

namespace Dispatchly;

internal sealed class SqlServerMessagePublisher : IMessagePublisher
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly SqlServerMessageStore _store;
    private readonly SqlServerTransportOptions _options;

    public SqlServerMessagePublisher(
        IMessageTypeCatalog catalog,
        SqlServerMessageStore store,
        SqlServerTransportOptions options)
    {
        _catalog = catalog;
        _store = store;
        _options = options;
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
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
