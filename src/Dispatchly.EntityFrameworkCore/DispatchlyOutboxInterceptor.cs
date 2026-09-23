using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dispatchly.EntityFrameworkCore;

/// <summary>Writes tracked domain events into the outbox on the save transaction.</summary>
public sealed class DispatchlyOutboxInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly IMessageOutbox _outbox;
    private readonly ConditionalWeakTable<DbContext, OutboxEnlistment> _enlistments = new();

    /// <summary>Creates an interceptor that enlists through <paramref name="outbox" />.</summary>
    public DispatchlyOutboxInterceptor(IMessageOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _outbox = outbox;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            EnlistPendingAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            await EnlistPendingAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            CommitOwnedAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            await CommitOwnedAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            RollbackOwnedAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is not null)
        {
            await RollbackOwnedAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        ClearSources(eventData.Context);

    /// <inheritdoc />
    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearSources(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        ForgetEnlisted(eventData.Context);

    /// <inheritdoc />
    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ForgetEnlisted(eventData.Context);
        return Task.CompletedTask;
    }

    private async ValueTask EnlistPendingAsync(DbContext context, CancellationToken cancellationToken)
    {
        _enlistments.TryGetValue(context, out var enlistment);
        if (enlistment is { InProgress: true })
        {
            return;
        }

        var pending = Collect(context, enlistment);
        if (pending.Count == 0)
        {
            return;
        }

        if (enlistment is null)
        {
            enlistment = new OutboxEnlistment();
            _enlistments.Add(context, enlistment);
        }

        enlistment.InProgress = true;
        try
        {
            if (context.Database.CurrentTransaction is null)
            {
                await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                enlistment.OwnsTransaction = true;
            }

            var transaction = context.Database.CurrentTransaction?.GetDbTransaction()
                ?? throw new InvalidOperationException("SaveChanges has no database transaction for the outbox insert.");

            var started = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var (source, domainEvent) in pending)
            {
                Track(enlistment, source);
                if (!started.Add(domainEvent) || !enlistment.Enlisted.Add(domainEvent))
                {
                    continue;
                }

                try
                {
                    await OutboxInvoker.EnlistAsync(_outbox, domainEvent, transaction, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    enlistment.Enlisted.Remove(domainEvent);
                    throw;
                }
            }
        }
        finally
        {
            enlistment.InProgress = false;
        }
    }

    private static List<(IDomainEventSource Source, object Event)> Collect(DbContext context, OutboxEnlistment? enlistment)
    {
        var pending = new List<(IDomainEventSource Source, object Event)>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IDomainEventSource source)
            {
                continue;
            }

            if (source.DomainEvents is null)
            {
                throw new InvalidOperationException(
                    $"'{source.GetType().FullName}' returned a null domain event collection.");
            }

            foreach (var domainEvent in source.DomainEvents)
            {
                if (domainEvent is null)
                {
                    throw new InvalidOperationException(
                        $"'{source.GetType().FullName}' returned a null domain event.");
                }

                if (enlistment is not null && enlistment.Enlisted.Contains(domainEvent))
                {
                    Track(enlistment, source);
                    continue;
                }

                pending.Add((source, domainEvent));
            }
        }

        return pending;
    }

    private async ValueTask CommitOwnedAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (!_enlistments.TryGetValue(context, out var enlistment) || !enlistment.OwnsTransaction)
        {
            return;
        }

        if (context.Database.CurrentTransaction is null)
        {
            return;
        }

        await context.Database.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        ClearSources(context);
    }

    private async ValueTask RollbackOwnedAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (!_enlistments.TryGetValue(context, out var enlistment) || !enlistment.OwnsTransaction)
        {
            return;
        }

        enlistment.OwnsTransaction = false;
        if (context.Database.CurrentTransaction is not null)
        {
            await context.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearSources(DbContext? context)
    {
        if (context is null || !_enlistments.TryGetValue(context, out var enlistment))
        {
            return;
        }

        foreach (var source in enlistment.Sources)
        {
            source.ClearDomainEvents();
        }

        _enlistments.Remove(context);
    }

    private void ForgetEnlisted(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        _enlistments.Remove(context);
    }

    private static void Track(OutboxEnlistment enlistment, IDomainEventSource source)
    {
        foreach (var existing in enlistment.Sources)
        {
            if (ReferenceEquals(existing, source))
            {
                return;
            }
        }

        enlistment.Sources.Add(source);
    }

    private sealed class OutboxEnlistment
    {
        public bool OwnsTransaction { get; set; }

        public bool InProgress { get; set; }

        public List<IDomainEventSource> Sources { get; } = [];

        public HashSet<object> Enlisted { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static class OutboxInvoker
    {
        private static readonly ConcurrentDictionary<Type, Func<IMessageOutbox, object, DbTransaction, CancellationToken, ValueTask<MessageId>>> Cache = new();

        public static ValueTask<MessageId> EnlistAsync(
            IMessageOutbox outbox,
            object message,
            DbTransaction transaction,
            CancellationToken cancellationToken)
        {
            var invoke = Cache.GetOrAdd(message.GetType(), static type =>
            {
                var method = typeof(OutboxInvoker)
                    .GetMethod(nameof(EnlistTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(type);
                return method.CreateDelegate<Func<IMessageOutbox, object, DbTransaction, CancellationToken, ValueTask<MessageId>>>();
            });
            return invoke(outbox, message, transaction, cancellationToken);
        }

        private static ValueTask<MessageId> EnlistTyped<TMessage>(
            IMessageOutbox outbox,
            object message,
            DbTransaction transaction,
            CancellationToken cancellationToken) =>
            outbox.EnlistAsync((TMessage)message, transaction, cancellationToken);
    }
}
