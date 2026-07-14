namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// Raised by an <see cref="Entity{TId}"/> within a single module. Handled in-process,
/// same transaction. Never crosses a module boundary — for that, publish an
/// <see cref="IIntegrationEvent"/> via <see cref="IEventBus"/> instead.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredOnUtc { get; }
}
