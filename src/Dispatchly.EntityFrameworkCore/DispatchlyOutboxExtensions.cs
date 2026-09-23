using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatchly.EntityFrameworkCore;

/// <summary>Registers the Dispatchly outbox with an Entity Framework context.</summary>
public static class DispatchlyOutboxExtensions
{
    /// <summary>
    /// Enlists <see cref="IDomainEventSource" /> events on the current database transaction during
    /// <see cref="DbContext.SaveChangesAsync(System.Threading.CancellationToken)" />.
    /// Resolve <paramref name="serviceProvider" /> from the same container that registered one durable transport.
    /// The context must use that transport's database, and the host must be started so the outbox tables exist.
    /// When the save has no transaction, this begins one and commits it after the save succeeds.
    /// That save then runs outside a retrying execution strategy.
    /// Events stay on the source until the transaction commits. A rollback leaves them in place.
    /// </summary>
    public static DbContextOptionsBuilder UseDispatchlyOutbox(
        this DbContextOptionsBuilder options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        options.AddInterceptors(new DispatchlyOutboxInterceptor(serviceProvider.GetRequiredService<IMessageOutbox>()));
        return options;
    }
}
