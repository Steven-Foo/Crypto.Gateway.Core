using System.Reflection;
using System.Text.Json;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Persistence;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Infrastructure.Outbox;

/// <summary>
/// Relays a module's Outbox to <see cref="IEventBus"/> (§7.5): the durable half of the outbox pattern.
/// The business change and its outbox row commit together; this dispatcher then publishes each
/// unprocessed row and marks it done. Delivery is <b>at-least-once</b> — if we publish then crash before
/// marking, the row re-publishes — which is safe because every consumer is idempotent (e.g. the Ledger
/// dedups on the business reference). A per-module distributed lock single-flights dispatch across host
/// instances; losing the lock never loses a message (the row simply waits for the next poll).
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    IDistributedLockFactory lockFactory,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : ModuleDbContext
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    private static readonly MethodInfo PublishMethod =
        typeof(IEventBus).GetMethod(nameof(IEventBus.PublishAsync))!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch pass failed for {Context}; will retry.", typeof(TContext).Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Dispatches one batch of pending outbox messages. Public so a host or a test can flush on demand.</summary>
    public async Task DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Single-flight per module so two hosts don't double-publish the same batch.
        await using var _ = await lockFactory.AcquireAsync($"outbox:{context.Schema}", LockTimeout, cancellationToken);

        var messages = await context.OutboxMessages
            .Where(m => m.ProcessedOnUtc == null)
            .OrderBy(m => EF.Property<long>(m, "Seq"))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
            return;

        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var now = timeProvider.GetUtcNow();

        foreach (var message in messages)
        {
            try
            {
                var eventType = Type.GetType(message.Type)
                    ?? throw new InvalidOperationException($"Cannot resolve event type '{message.Type}'.");
                var @event = JsonSerializer.Deserialize(message.Content, eventType)
                    ?? throw new InvalidOperationException($"Event content for {message.Id} deserialized to null.");

                await PublishAsync(eventBus, eventType, @event, cancellationToken);
                message.MarkProcessed(now);
            }
            catch (Exception ex)
            {
                // Leave the row unprocessed so the next poll retries it; record the last error for ops.
                logger.LogError(ex, "Failed to dispatch outbox message {MessageId} ({Type}).", message.Id, message.Type);
                message.MarkFailed(ex.Message);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static Task PublishAsync(IEventBus eventBus, Type eventType, object @event, CancellationToken cancellationToken) =>
        (Task)PublishMethod.MakeGenericMethod(eventType).Invoke(eventBus, [@event, cancellationToken])!;

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public static class OutboxDispatcherExtensions
{
    /// <summary>Runs the outbox dispatcher for a module's DbContext. Register once per module that publishes events.</summary>
    public static IServiceCollection AddOutboxDispatcher<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        services.AddHostedService<OutboxDispatcher<TContext>>();
        return services;
    }
}
