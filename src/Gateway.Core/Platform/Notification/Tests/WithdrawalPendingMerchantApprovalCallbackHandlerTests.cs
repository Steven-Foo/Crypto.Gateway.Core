using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Tests;

/// <summary>
/// The "a payout needs your approval" webhook. The defect it guards: a payout submitted in the merchant portal
/// carries no callback URL of its own, so before the fallback no notification was ever sent for exactly the
/// payouts this webhook exists for.
/// </summary>
public sealed class WithdrawalPendingMerchantApprovalCallbackHandlerTests
{
    private static readonly Guid MerchantId = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMerchantCallbackSigner _signer = Substitute.For<IMerchantCallbackSigner>();
    private readonly ICallbackDeliveryScheduler _scheduler = Substitute.For<ICallbackDeliveryScheduler>();
    private readonly IMerchantDirectory _merchants = Substitute.For<IMerchantDirectory>();

    public WithdrawalPendingMerchantApprovalCallbackHandlerTests() =>
        _signer.SignAsync(MerchantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CallbackSignature("1700000000", "abcd")));

    private WithdrawalPendingMerchantApprovalCallbackHandler Handler => new(
        _signer, _scheduler, _merchants, NullLogger<WithdrawalPendingMerchantApprovalCallbackHandler>.Instance);

    private static WithdrawalPendingMerchantApproval Event(string? callbackUrl) => new(
        Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), MerchantId, Guid.CreateVersion7(),
        "5000000", "100000", "TDestination", DateTimeOffset.UtcNow, "PO-1", callbackUrl);

    private void SavedCallbackUrl(string? url) =>
        _merchants.FindByIdAsync(MerchantId, Arg.Any<CancellationToken>())
            .Returns(new MerchantSummary(MerchantId, "ACME", "Acme", url, CanTransact: true));

    [Fact]
    public async Task A_portal_payout_with_no_url_of_its_own_is_sent_to_the_merchants_saved_callback_url()
    {
        SavedCallbackUrl("https://merchant.test/saved");

        await Handler.HandleAsync(Event(callbackUrl: null), Ct);

        await _scheduler.Received(1).ScheduleAsync(
            CallbackReferenceType.Withdrawal, Arg.Any<Guid>(), "https://merchant.test/saved",
            Arg.Is<string>(b => b.Contains("pending_merchant_approval")), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_payouts_own_url_wins_over_the_saved_one()
    {
        SavedCallbackUrl("https://merchant.test/saved");

        await Handler.HandleAsync(Event("https://merchant.test/per-payout"), Ct);

        await _scheduler.Received(1).ScheduleAsync(
            Arg.Any<CallbackReferenceType>(), Arg.Any<Guid>(), "https://merchant.test/per-payout",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _merchants.DidNotReceiveWithAnyArgs().FindByIdAsync(default, Ct);
    }

    [Fact]
    public async Task With_no_url_anywhere_nothing_is_sent()
    {
        SavedCallbackUrl(null);

        await Handler.HandleAsync(Event(callbackUrl: null), Ct);

        await _scheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default, default, default!, default!, default!, default!, default!, Ct);
    }
}
