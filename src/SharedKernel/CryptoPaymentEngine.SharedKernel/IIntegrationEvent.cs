namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// A module's public, cross-module event contract. Published through <see cref="IEventBus"/>
/// and, today, delivered in-process; the shape must stay broker-agnostic since it is what
/// gets serialized onto Kafka once a module is extracted into its own service.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}
