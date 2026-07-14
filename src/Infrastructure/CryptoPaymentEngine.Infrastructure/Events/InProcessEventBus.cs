using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Infrastructure.Events;

/// <summary>
/// Today's <see cref="IEventBus"/> binding: handlers run in-process, resolved from DI. Swapping
/// this for a Kafka producer later is the only change needed to move a module out of process —
/// no module code references this type directly.
/// </summary>
public sealed class InProcessEventBus(IServiceProvider serviceProvider, ILogger<InProcessEventBus> logger) : IEventBus
{
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        var handlers = serviceProvider.GetServices<IIntegrationEventHandler<TEvent>>();

        // Run every handler so one failure never starves the others (§7.5), but collect failures and
        // surface them: the caller (the outbox dispatcher) must learn a handler failed so it leaves the
        // message unprocessed and retries. Handlers are idempotent, so re-running the ones that succeeded
        // is safe.
        List<Exception>? failures = null;

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(@event, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Integration event handler {Handler} failed for {Event} ({EventId})",
                    handler.GetType().Name,
                    typeof(TEvent).Name,
                    @event.EventId);

                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
            throw new AggregateException($"{failures.Count} handler(s) failed for {typeof(TEvent).Name}.", failures);
    }
}
