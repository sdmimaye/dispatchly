using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Dispatchly.Transport.SqlServer;

internal sealed class SqlServerMessageOutbox : IMessageOutbox
{
    private readonly IMessageTypeCatalog _catalog;
    private readonly SqlServerMessageStore _store;

    public SqlServerMessageOutbox(IMessageTypeCatalog catalog, SqlServerMessageStore store)
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
        if (transaction is not SqlTransaction sqlTransaction)
        {
            throw new ArgumentException("SQL Server outbox enlistment requires a SqlTransaction.", nameof(transaction));
        }

        var connection = sqlTransaction.Connection
            ?? throw new ArgumentException("The transaction is not associated with a connection.", nameof(transaction));
        var registration = _catalog.GetRequired(typeof(TMessage));
        var id = MessageId.New();
        await _store.InsertAsync(
            connection,
            sqlTransaction,
            registration.TableName,
            id.Value,
            registration.Serialize(message),
            cancellationToken).ConfigureAwait(false);
        return id;
    }
}
