namespace Dispatchly.Abstractions;

/// <summary>Exposes domain events an Entity Framework save can enlist on the outbox.</summary>
public interface IDomainEventSource
{
    /// <summary>Events raised by this instance that are still waiting for the outbox transaction to commit.</summary>
    IReadOnlyCollection<object> DomainEvents { get; }

    /// <summary>Removes the events after the outbox transaction commits.</summary>
    void ClearDomainEvents();
}
