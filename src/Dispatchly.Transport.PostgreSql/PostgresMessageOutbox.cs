using System.Data.Common;
using Npgsql;

namespace Dispatchly.Transport.PostgreSql;

internal sealed class PostgresMessageOutbox : IMessageOutbox
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly PostgresMessageStore _store;

    public PostgresMessageOutbox(IMessageTypeCatalog catalog, PostgresMessageStore store)
    {
        _catalog = catalog;
        _store = store;
    }

    public async ValueTask<MessageId> EnlistAsync<TMessage>(
        TMessage message,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction npgsqlTransaction)
        {
            throw new ArgumentException("PostgreSQL outbox enlistment requires an NpgsqlTransaction.", nameof(transaction));
        }

        var connection = npgsqlTransaction.Connection
            ?? throw new ArgumentException("The transaction is not associated with a connection.", nameof(transaction));
        var registration = _catalog.GetRequired(typeof(TMessage));
        var id = MessageId.New();
        await _store.InsertAsync(
            connection,
            npgsqlTransaction,
            registration.TableName,
            id.Value,
            registration.Serialize(message),
            cancellationToken).ConfigureAwait(false);
        return id;
    }
}
