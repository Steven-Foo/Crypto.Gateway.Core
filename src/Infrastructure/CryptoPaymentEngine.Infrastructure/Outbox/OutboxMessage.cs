using System.Text.Json;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Infrastructure.Outbox;

/// <summary>
/// Durable record of an <see cref="IIntegrationEvent"/>, written in the same DB transaction as
/// the business change that raised it. Each module's DbContext owns its own outbox table in its
/// own schema; a dispatcher relays unprocessed rows to <see cref="IEventBus"/>.
/// Append-only apart from the dispatch-result columns — no rowversion.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private init; }
    public string Type { get; private init; } = null!;
    public string Content { get; private init; } = null!;
    public DateTimeOffset OccurredOnUtc { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? ProcessedOnUtc { get; private set; }
    public int RetryCount { get; private set; }
    public string? Error { get; private set; }

    private OutboxMessage()
    {
    }

    public static OutboxMessage From(IIntegrationEvent integrationEvent, TimeProvider? timeProvider = null)
    {
        var type = integrationEvent.GetType();
        return new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            Content = JsonSerializer.Serialize(integrationEvent, type),
            OccurredOnUtc = integrationEvent.OccurredOnUtc,
            CreatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
        };
    }

    public void MarkProcessed(DateTimeOffset processedOnUtc)
    {
        ProcessedOnUtc = processedOnUtc;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        RetryCount++;
        Error = error;
    }
}
