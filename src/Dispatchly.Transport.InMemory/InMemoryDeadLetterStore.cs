using System.Collections.Concurrent;

namespace Dispatchly.Transport.InMemory;

internal sealed class InMemoryDeadLetterStore : IInMemoryDeadLetterStore
{
    private readonly ConcurrentQueue<InMemoryDeadLetter> _items = new();

    public void Add(InMemoryDeadLetter deadLetter) => _items.Enqueue(deadLetter);

    public IReadOnlyList<InMemoryDeadLetter> Snapshot() => _items.ToArray();
}
