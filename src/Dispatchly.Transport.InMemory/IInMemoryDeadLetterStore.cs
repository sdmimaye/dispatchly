namespace Dispatchly;

/// <summary>In-process dead letters. They disappear when the process exits.</summary>
public interface IInMemoryDeadLetterStore
{
    /// <summary>Returns a snapshot of dead-lettered messages.</summary>
    IReadOnlyList<InMemoryDeadLetter> Snapshot();
}
