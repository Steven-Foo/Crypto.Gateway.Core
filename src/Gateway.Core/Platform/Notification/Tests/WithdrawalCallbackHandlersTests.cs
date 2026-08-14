using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
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

/// <summary>Mirrors <c>DepositCallbackHandlerTests</c> — same schedule-don't-send contract, same
/// <c>CallbackReferenceType.Withdrawal</c> wiring, just the withdrawal-side events.</summary>
public sealed class WithdrawalCallbackHandlersTests
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid WithdrawalId = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WithdrawalConfirmed Confirmed(string? callbackUrl = "https://merchant.test/callback") => new(
        Guid.CreateVersion7(), DateTimeOffset.UtcNow, WithdrawalId, Merchant, Asset,
        AmountBaseUnits: "3000000", FeeBaseUnits: "100000", TransactionHash: "0xwithdrawtx",
        ConfirmedAt: DateTimeOffset.UtcNow, MerchantTransactionId: "wd-1", DestinationAddress: "TDestAddress", callbackUrl);

    private static WithdrawalFailed Failed(string? callbackUrl = "https://merchant.test/callback") => new(
        Guid.CreateVersion7(), DateTimeOffset.UtcNow, WithdrawalId, Merchant, Asset,
        AmountBaseUnits: "3000000", FeeBaseUnits: "100000", Reason: "insufficient balance",
        FailedAt: DateTimeOffset.UtcNow, MerchantTransactionId: "wd-1", callbackUrl);

    private sealed class Captured
    {
        public string? SignedBody;
        public string? ScheduledUrl;
        public string? ScheduledBody;
        public CallbackReferenceType? ScheduledReferenceType;
        public Guid? ScheduledReferenceId;
    }

    private static ICallbackDeliveryScheduler BuildScheduler(Captured captured)
    {
        var scheduler = Substitute.For<ICallbackDeliveryScheduler>();
        scheduler.ScheduleAsync(
                Arg.Do<CallbackReferenceType>(t => captured.ScheduledReferenceType = t),
                Arg.Do<Guid>(id => captured.ScheduledReferenceId = id),
                Arg.Do<string>(u => captured.ScheduledUrl = u),
                Arg.Do<string>(b => captured.ScheduledBody = b),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return scheduler;
    }

    private static IMerchantCallbackSigner BuildSigner(Captured captured, bool canSign) =>
        BuildSignerFor(captured, canSign);

    private static IMerchantCallbackSigner BuildSignerFor(Captured captured, bool canSign)
    {
        var signer = Substitute.For<IMerchantCallbackSigner>();
        signer.SignAsync(Arg.Any<Guid>(), Arg.Do<string>(b => captured.SignedBody = b), Arg.Any<CancellationToken>())
            .Returns(canSign
                ? Result.Success(new CallbackSignature("1700000000", "deadbeef"))
                : Result.Failure<CallbackSignature>(Error.NotFound("merchant.credential_not_found", "No credential.")));
        return signer;
    }

    [Fact]
    public async Task Confirmed_schedules_the_frozen_withdraw_payload_with_the_withdrawal_reference_type()
    {
        var captured = new Captured();
        var scheduler = BuildScheduler(captured);
        var signer = BuildSigner(captured, canSign: true);
        var assets = Substitute.For<IAssetCatalog>();
        assets.FindByIdAsync(Asset, Arg.Any<CancellationToken>())
            .Returns(new AssetDto(Asset, Chain.Tron, "USDT", "TContract", Decimals: 6, IsNative: false));

        var handler = new WithdrawalConfirmedCallbackHandler(signer, assets, scheduler, NullLogger<WithdrawalConfirmedCallbackHandler>.Instance);
        var @event = Confirmed();

        await handler.HandleAsync(@event, Ct);

        captured.ScheduledReferenceType.ShouldBe(CallbackReferenceType.Withdrawal);
        captured.ScheduledReferenceId.ShouldBe(WithdrawalId);
        captured.ScheduledUrl.ShouldBe("https://merchant.test/callback");
        captured.SignedBody.ShouldBe(captured.ScheduledBody);

        using var doc = JsonDocument.Parse(captured.ScheduledBody!);
        doc.RootElement.GetProperty("transactionId").GetString().ShouldBe("wd-1");
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe("withdraw"); // partner vocabulary, not the internal enum name
        data.GetProperty("status").GetString().ShouldBe("confirmed");
        data.GetProperty("txHash").GetString().ShouldBe("0xwithdrawtx");
        data.GetProperty("toAddress").GetString().ShouldBe("TDestAddress");
        data.GetProperty("amount").GetDecimal().ShouldBe(3m); // 3_000_000 base units / 1e6
        data.GetProperty("currencyCode").GetString().ShouldBe("USDT");
    }

    [Fact]
    public async Task Confirmed_does_not_schedule_when_the_merchant_has_no_callback_url()
    {
        var captured = new Captured();
        var scheduler = BuildScheduler(captured);
        var signer = BuildSigner(captured, canSign: true);
        var assets = Substitute.For<IAssetCatalog>();

        var handler = new WithdrawalConfirmedCallbackHandler(signer, assets, scheduler, NullLogger<WithdrawalConfirmedCallbackHandler>.Instance);
        await handler.HandleAsync(Confirmed(callbackUrl: null), Ct);

        await scheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<CallbackReferenceType>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_does_not_schedule_an_unsigned_callback_when_there_is_no_credential()
    {
        var captured = new Captured();
        var scheduler = BuildScheduler(captured);
        var signer = BuildSigner(captured, canSign: false);
        var assets = Substitute.For<IAssetCatalog>();

        var handler = new WithdrawalConfirmedCallbackHandler(signer, assets, scheduler, NullLogger<WithdrawalConfirmedCallbackHandler>.Instance);
        await handler.HandleAsync(Confirmed(), Ct); // logs and drops — never schedules unsigned

        await scheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<CallbackReferenceType>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_schedules_the_frozen_withdraw_fail_payload()
    {
        var captured = new Captured();
        var scheduler = BuildScheduler(captured);
        var signer = BuildSigner(captured, canSign: true);

        var handler = new WithdrawalFailedCallbackHandler(signer, scheduler, NullLogger<WithdrawalFailedCallbackHandler>.Instance);
        var @event = Failed();

        await handler.HandleAsync(@event, Ct);

        captured.ScheduledReferenceType.ShouldBe(CallbackReferenceType.Withdrawal);
        captured.ScheduledReferenceId.ShouldBe(WithdrawalId);

        using var doc = JsonDocument.Parse(captured.ScheduledBody!);
        doc.RootElement.GetProperty("transactionId").GetString().ShouldBe("wd-1");
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe("withdraw");
        data.GetProperty("status").GetString().ShouldBe("failed");
        data.GetProperty("reason").GetString().ShouldBe("insufficient balance");
    }

    [Fact]
    public async Task Failed_does_not_schedule_when_the_merchant_has_no_callback_url()
    {
        var captured = new Captured();
        var scheduler = BuildScheduler(captured);
        var signer = BuildSigner(captured, canSign: true);

        var handler = new WithdrawalFailedCallbackHandler(signer, scheduler, NullLogger<WithdrawalFailedCallbackHandler>.Instance);
        await handler.HandleAsync(Failed(callbackUrl: null), Ct);

        await scheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<CallbackReferenceType>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
