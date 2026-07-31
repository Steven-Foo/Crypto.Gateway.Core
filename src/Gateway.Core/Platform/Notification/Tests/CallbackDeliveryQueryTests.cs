using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Tests;

/// <summary>Real SQL Server, proving the Ops-facing projection carries the failed-attempt count and the
/// exact next-attempt time — not just the status string.</summary>
public sealed class CallbackDeliveryQueryTests : IAsyncLifetime
{
    private const string DbName = "CpeCallbackDeliveryQueryTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    public async ValueTask InitializeAsync()
    {
        await using var context = BuildContext();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = BuildContext();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    private static NotificationDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseSqlServer(ConnectionString).Options);

    [Fact]
    public async Task A_delivery_that_has_failed_twice_reports_the_failed_count_and_the_exact_next_attempt_time()
    {
        var referenceId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var delivery = CallbackDelivery.Schedule(
            CallbackReferenceType.Deposit, referenceId, "https://merchant.test/cb", "{}", "crypto-transaction", "1700000000", "sig", now);
        delivery.RecordFailure("timeout", now, CallbackDeliveryProcessingBackoff.Schedule);       // attempt 1 failed → next in 30s
        delivery.RecordFailure("timeout", now.AddSeconds(30), CallbackDeliveryProcessingBackoff.Schedule); // attempt 2 failed → next in 1m

        await using (var context = BuildContext())
        {
            context.CallbackDeliveries.Add(delivery);
            await context.SaveChangesAsync(Ct);
        }

        await using var reader = BuildContext();
        var query = new CallbackDeliveryQuery(reader);
        var statuses = await query.GetStatusesAsync(CallbackReferenceType.Deposit, [referenceId], Ct);

        var view = statuses[referenceId];
        view.Status.ShouldBe("PendingNotification");
        view.AttemptCount.ShouldBe(2);
        view.NextAttemptAt.ShouldBe(delivery.NextAttemptAt);
        view.NextAttemptAt.ShouldBe(now.AddSeconds(30).Add(CallbackDeliveryProcessingBackoff.Schedule[1])); // 1 minute after the 2nd failure
    }

    [Fact]
    public async Task A_reference_with_no_scheduled_delivery_reports_zero_and_no_next_attempt()
    {
        await using var reader = BuildContext();
        var query = new CallbackDeliveryQuery(reader);
        var statuses = await query.GetStatusesAsync(CallbackReferenceType.Withdrawal, [Guid.CreateVersion7()], Ct);

        var view = statuses.Values.Single();
        view.Status.ShouldBeNull();
        view.AttemptCount.ShouldBe(0);
        view.NextAttemptAt.ShouldBeNull();
    }
}
