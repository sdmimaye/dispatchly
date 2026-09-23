namespace Dispatchly.Abstractions;

/// <summary>
/// Stores domain events for an aggregate.
/// <see cref="IDomainEventSource.DomainEvents" /> is implemented explicitly so Entity Framework does not map the collection.
/// </summary>
public abstract class DomainEventSource : IDomainEventSource
{
    private readonly List<object> _events = [];

    /// <summary>
    /// Records <paramref name="domainEvent" /> until the outbox transaction commits.
    /// Call this from the aggregate's own methods.
    /// </summary>
    protected void Raise(object domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _events.Add(domainEvent);
    }

    /// <inheritdoc />
    IReadOnlyCollection<object> IDomainEventSource.DomainEvents => _events;

    /// <inheritdoc />
    void IDomainEventSource.ClearDomainEvents() => _events.Clear();
}
