namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// The only way one module may signal another. Today <c>Infrastructure</c> binds this to an
/// in-process dispatcher backed by the Outbox; swapping to a Kafka producer later must not
/// require any module to change (§7.5 of CLAUDE.md).
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}
