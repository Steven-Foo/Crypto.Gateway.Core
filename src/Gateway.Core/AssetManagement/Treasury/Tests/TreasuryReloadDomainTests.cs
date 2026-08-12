using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

public sealed class TreasuryReloadDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid TargetWallet = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("5000000");

    private static TreasuryReload Initiated() =>
        TreasuryReload.Initiate(Chain.Tron, Asset, "TCold", TargetWallet, "THot", Amount, [1, 2, 3], Now).Value;

    [Fact]
    public void Initiate_starts_awaiting_signature_and_carries_the_unsigned_payload()
    {
        var reload = Initiated();
        reload.Status.ShouldBe(TreasuryReloadStatus.AwaitingSignature);
        reload.SourceAddress.ShouldBe("TCold");
        reload.TargetAddress.ShouldBe("THot");
        reload.Amount.ShouldBe(Amount);
        reload.UnsignedPayload.ShouldBe([1, 2, 3]);
        reload.HasSignedTransaction.ShouldBeFalse();
    }

    [Theory]
    [InlineData("0")]
    public void Initiate_rejects_a_non_positive_amount(string amount) =>
        TreasuryReload.Initiate(Chain.Tron, Asset, "TCold", TargetWallet, "THot", BigInteger.Parse(amount), [1], Now)
            .Error!.Code.ShouldBe(TreasuryReloadErrors.AmountNotPositive.Code);

    [Fact]
    public void Initiate_requires_an_unsigned_payload() =>
        TreasuryReload.Initiate(Chain.Tron, Asset, "TCold", TargetWallet, "THot", Amount, [], Now)
            .Error!.Code.ShouldBe(TreasuryReloadErrors.UnsignedPayloadRequired.Code);

    [Fact]
    public void The_happy_path_runs_awaiting_signed_broadcast_confirmed()
    {
        var reload = Initiated();

        reload.SubmitSigned([9, 9], Now).IsSuccess.ShouldBeTrue();
        reload.Status.ShouldBe(TreasuryReloadStatus.Signed);
        reload.HasSignedTransaction.ShouldBeTrue();

        reload.MarkBroadcast("0xtx", Now).IsSuccess.ShouldBeTrue();
        reload.Status.ShouldBe(TreasuryReloadStatus.Broadcast);
        reload.TransactionHash.ShouldBe("0xtx");

        reload.Confirm(Now).IsSuccess.ShouldBeTrue();
        reload.Status.ShouldBe(TreasuryReloadStatus.Confirmed);
    }

    [Fact]
    public void Submit_requires_a_non_empty_signed_blob()
    {
        var reload = Initiated();
        reload.SubmitSigned([], Now).Error!.Code.ShouldBe(TreasuryReloadErrors.SignedPayloadRequired.Code);
        reload.Status.ShouldBe(TreasuryReloadStatus.AwaitingSignature);
    }

    [Fact]
    public void Fail_is_allowed_before_broadcast()
    {
        var awaiting = Initiated();
        awaiting.Fail("operator cancelled", Now).IsSuccess.ShouldBeTrue();
        awaiting.Status.ShouldBe(TreasuryReloadStatus.Failed);

        var signed = Initiated();
        signed.SubmitSigned([9], Now);
        signed.Fail("bad tx", Now).IsSuccess.ShouldBeTrue();
        signed.Status.ShouldBe(TreasuryReloadStatus.Failed);
    }

    [Fact]
    public void Fail_after_broadcast_is_refused_because_funds_may_be_on_chain()
    {
        var reload = Initiated();
        reload.SubmitSigned([9], Now);
        reload.MarkBroadcast("0xtx", Now);

        reload.Fail("timeout", Now).Error!.Code.ShouldBe(TreasuryReloadErrors.InvalidStateTransition.Code);
        reload.Status.ShouldBe(TreasuryReloadStatus.Broadcast); // unchanged
    }

    [Fact]
    public void Out_of_order_transitions_are_refused()
    {
        var reload = Initiated();
        reload.MarkBroadcast("0xtx", Now).Error!.Code.ShouldBe(TreasuryReloadErrors.InvalidStateTransition.Code); // can't broadcast before signed
        reload.Confirm(Now).Error!.Code.ShouldBe(TreasuryReloadErrors.InvalidStateTransition.Code);                // can't confirm before broadcast
    }
}
