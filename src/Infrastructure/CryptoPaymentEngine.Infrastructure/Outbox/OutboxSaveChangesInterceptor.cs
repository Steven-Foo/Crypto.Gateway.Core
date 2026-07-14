using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CryptoPaymentEngine.Infrastructure.Outbox;

/// <summary>
/// Registered on a module's own DbContext (<c>optionsBuilder.AddInterceptors(...)</c>). Before
/// every SaveChanges, drains domain events raised on tracked entities and, for the ones that
/// also implement <see cref="IIntegrationEvent"/> (i.e. are meant to cross a module boundary),
/// writes an <see cref="OutboxMessage"/> row in the same transaction as the business change.
/// Purely domain-internal events (no <see cref="IIntegrationEvent"/>) are dropped after being
/// raised — dispatch those, if needed, within the module before SaveChanges instead.
/// </summary>
public sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            EnqueueOutboxMessages(context);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void EnqueueOutboxMessages(DbContext context)
    {
        var entitiesWithEvents = context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        foreach (var entity in entitiesWithEvents)
        {
            foreach (var integrationEvent in entity.DomainEvents.OfType<IIntegrationEvent>())
            {
                context.Add(OutboxMessage.From(integrationEvent));
            }

            entity.ClearDomainEvents();
        }
    }
}
