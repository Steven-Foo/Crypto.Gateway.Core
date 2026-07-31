using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Tests;

public sealed class CallbackDeliveryResendServiceTests
{
    private static readonly Guid ReferenceId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CallbackDelivery Scheduled() =>
        CallbackDelivery.Schedule(CallbackReferenceType.Deposit, ReferenceId, "https://merchant.test/cb", "{}", "crypto-transaction", "1700000000", "sig", Now);

    private static CallbackDelivery Abandoned()
    {
        var d = Scheduled();
        for (var i = 0; i < CallbackDeliveryProcessingBackoff.Schedule.Count + 1; i++)
            d.RecordFailure("boom", Now, CallbackDeliveryProcessingBackoff.Schedule);
        d.Status.ShouldBe(CallbackDeliveryStatus.Abandoned); // sanity-check the test fixture itself
        return d;
    }

    [Fact]
    public async Task A_pending_delivery_is_refused_WITHOUT_ever_contacting_the_merchant()
    {
        var delivery = Scheduled(); // still Pending — automatic retries haven't given up
        var repository = Substitute.For<ICallbackDeliveryRepository>();
        repository.FindAsync(CallbackReferenceType.Deposit, ReferenceId, Arg.Any<CancellationToken>()).Returns(delivery);
        var sender = Substitute.For<IWebhookSender>();

        var service = new CallbackDeliveryResendService(repository, sender, TimeProvider.System, NullLogger<CallbackDeliveryResendService>.Instance);
        var result = await service.ResendAsync(CallbackReferenceType.Deposit, ReferenceId, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(CallbackDeliveryErrors.NotAbandoned.Code);

        // The regression this guards: the guard must gate BEFORE sending, not just before recording —
        // a premature call must fire zero real webhooks, not send one and merely fail to save the outcome.
        await sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_abandoned_delivery_that_succeeds_is_marked_notified()
    {
        var delivery = Abandoned();
        var repository = Substitute.For<ICallbackDeliveryRepository>();
        repository.FindAsync(CallbackReferenceType.Deposit, ReferenceId, Arg.Any<CancellationToken>()).Returns(delivery);
        var sender = Substitute.For<IWebhookSender>();
        sender.SendAsync(
                delivery.CallbackUrl, delivery.Body, delivery.CallbackType, delivery.Timestamp, delivery.SignatureHex, Arg.Any<CancellationToken>())
            .Returns(true);

        var service = new CallbackDeliveryResendService(repository, sender, TimeProvider.System, NullLogger<CallbackDeliveryResendService>.Instance);
        var result = await service.ResendAsync(CallbackReferenceType.Deposit, ReferenceId, Ct);

        result.IsSuccess.ShouldBeTrue();
        delivery.Status.ShouldBe(CallbackDeliveryStatus.Notified);
        delivery.DeliveredAt.ShouldNotBeNull();
        await repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_abandoned_delivery_that_fails_again_stays_abandoned_and_is_resendable_again()
    {
        var delivery = Abandoned();
        var repository = Substitute.For<ICallbackDeliveryRepository>();
        repository.FindAsync(CallbackReferenceType.Deposit, ReferenceId, Arg.Any<CancellationToken>()).Returns(delivery);
        var sender = Substitute.For<IWebhookSender>();
        sender.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var service = new CallbackDeliveryResendService(repository, sender, TimeProvider.System, NullLogger<CallbackDeliveryResendService>.Instance);
        var result = await service.ResendAsync(CallbackReferenceType.Deposit, ReferenceId, Ct);

        result.IsSuccess.ShouldBeTrue(); // the RESEND ATTEMPT succeeded in running; the DELIVERY itself still failed
        delivery.Status.ShouldBe(CallbackDeliveryStatus.Abandoned); // no re-entry into the automatic backoff schedule
        delivery.LastError.ShouldNotBeNull();
    }

    [Fact]
    public async Task No_record_at_all_fails_with_not_found()
    {
        var repository = Substitute.For<ICallbackDeliveryRepository>();
        repository.FindAsync(CallbackReferenceType.Withdrawal, ReferenceId, Arg.Any<CancellationToken>()).Returns((CallbackDelivery?)null);
        var sender = Substitute.For<IWebhookSender>();

        var service = new CallbackDeliveryResendService(repository, sender, TimeProvider.System, NullLogger<CallbackDeliveryResendService>.Instance);
        var result = await service.ResendAsync(CallbackReferenceType.Withdrawal, ReferenceId, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(CallbackDeliveryErrors.NotFound.Code);
        await sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
