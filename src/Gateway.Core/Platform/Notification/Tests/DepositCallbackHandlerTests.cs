using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Tests;

public sealed class DepositCallbackHandlerTests
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentIntentMatched Matched(string? callbackUrl = "https://merchant.test/callback") => new(
        Guid.CreateVersion7(), DateTimeOffset.UtcNow, Merchant, Guid.CreateVersion7(), "tx-1", callbackUrl,
        Chain.Tron, Asset, "TDepositAddress", ExpectedAmountBaseUnits: "1000000", ActualAmountBaseUnits: "1000000",
        AmountMatched: true, DepositId: Guid.CreateVersion7(), TransactionHash: "0xdeadbeef", MatchedAt: DateTimeOffset.UtcNow);

    private sealed class Captured { public string? SignedBody; public string? SentBody; public string? SentUrl; }

    private static (DepositCallbackHandler Handler, IWebhookSender Sender) Build(
        Captured captured, bool delivered = true, bool canSign = true)
    {
        var signer = Substitute.For<IMerchantCallbackSigner>();
        signer.SignAsync(Arg.Any<Guid>(), Arg.Do<string>(b => captured.SignedBody = b), Arg.Any<CancellationToken>())
            .Returns(canSign
                ? Result.Success(new CallbackSignature("1700000000", "deadbeef"))
                : Result.Failure<CallbackSignature>(MerchantErrorsProbe.NoCredential));

        var assets = Substitute.For<IAssetCatalog>();
        assets.FindByIdAsync(Asset, Arg.Any<CancellationToken>())
            .Returns(new AssetDto(Asset, Chain.Tron, "USDT", "TContract", Decimals: 6, IsNative: false));

        var sender = Substitute.For<IWebhookSender>();
        sender.SendAsync(
                Arg.Do<string>(u => captured.SentUrl = u),
                Arg.Do<string>(b => captured.SentBody = b),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(delivered);

        return (new DepositCallbackHandler(signer, assets, sender, NullLogger<DepositCallbackHandler>.Instance), sender);
    }

    [Fact]
    public async Task It_posts_the_frozen_deposit_payload_and_the_signed_bytes_are_exactly_what_is_sent()
    {
        var captured = new Captured();
        var (handler, _) = Build(captured);

        await handler.HandleAsync(Matched(), Ct);

        captured.SentUrl.ShouldBe("https://merchant.test/callback");
        captured.SentBody.ShouldNotBeNull();
        captured.SignedBody.ShouldBe(captured.SentBody); // HMAC is over the exact body we send

        using var doc = JsonDocument.Parse(captured.SentBody!);
        doc.RootElement.GetProperty("transactionId").GetString().ShouldBe("tx-1");
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe("deposit");
        data.GetProperty("status").GetString().ShouldBe("confirmed");
        data.GetProperty("txHash").GetString().ShouldBe("0xdeadbeef");
        data.GetProperty("toAddress").GetString().ShouldBe("TDepositAddress");
        data.GetProperty("amountMatched").GetBoolean().ShouldBeTrue();
        data.GetProperty("amount").GetDecimal().ShouldBe(1m);          // 1_000_000 base units / 1e6
        data.GetProperty("expectedAmount").GetDecimal().ShouldBe(1m);
        data.GetProperty("currencyCode").GetString().ShouldBe("USDT");
    }

    [Fact]
    public async Task It_does_not_send_when_the_merchant_has_no_callback_url()
    {
        var captured = new Captured();
        var (handler, sender) = Build(captured);

        await handler.HandleAsync(Matched(callbackUrl: null), Ct);

        await sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_2xx_delivery_throws_so_the_outbox_retries()
    {
        var captured = new Captured();
        var (handler, _) = Build(captured, delivered: false);

        await Should.ThrowAsync<DomainException>(() => handler.HandleAsync(Matched(), Ct));
    }

    [Fact]
    public async Task It_does_not_send_an_unsigned_callback_when_there_is_no_credential()
    {
        var captured = new Captured();
        var (handler, sender) = Build(captured, canSign: false);

        await handler.HandleAsync(Matched(), Ct); // logs and drops — never throws, never sends unsigned

        await sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A stand-in error so the test does not depend on the Merchant module's internal error catalog.</summary>
    private static class MerchantErrorsProbe
    {
        public static readonly Error NoCredential = Error.NotFound("merchant.credential_not_found", "No credential.");
    }
}
