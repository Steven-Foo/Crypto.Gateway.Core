using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

public sealed class WithdrawalDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("3000000");
    private static readonly BigInteger Fee = BigInteger.Parse("100000");

    private static WithdrawalEntity Reserving() =>
        WithdrawalEntity.Request(Merchant, Asset, Chain.Tron, "TDest", Amount, Fee, "idem-1", Now).Value;

    private static WithdrawalEntity Approved(bool requiresApproval = false)
    {
        var w = Reserving();
        w.ConfirmReserved(requiresApproval, Now);
        return w;
    }

    [Fact]
    public void A_new_request_starts_in_reserving_until_funds_are_locked()
    {
        var w = Reserving();
        w.Status.ShouldBe(WithdrawalStatus.Reserving);
        w.Amount.ShouldBe(Amount);
        w.Fee.ShouldBe(Fee);
    }

    [Fact]
    public void Confirming_the_reserve_below_the_threshold_auto_approves() =>
        Approved(requiresApproval: false).Status.ShouldBe(WithdrawalStatus.Approved);

    [Fact]
    public void Confirming_the_reserve_above_the_threshold_awaits_approval() =>
        Approved(requiresApproval: true).Status.ShouldBe(WithdrawalStatus.PendingApproval);

    [Fact]
    public void A_refused_reserve_fails_the_withdrawal_without_a_release_event()
    {
        var w = Reserving();

        w.MarkReserveFailed("insufficient", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.Failed);
        w.DomainEvents.OfType<WithdrawalFailed>().ShouldBeEmpty(); // nothing was reserved → nothing to release
    }

    [Theory]
    [InlineData("", "idem", "withdrawal.destination_required")]
    [InlineData("TDest", "", "withdrawal.idempotency_key_required")]
    public void Request_validates_required_fields(string destination, string idem, string expected) =>
        WithdrawalEntity.Request(Merchant, Asset, Chain.Tron, destination, Amount, Fee, idem, Now)
            .Error!.Code.ShouldBe(expected);

    [Fact]
    public void Request_rejects_a_non_positive_amount() =>
        WithdrawalEntity.Request(Merchant, Asset, Chain.Tron, "TDest", BigInteger.Zero, Fee, "idem", Now)
            .Error!.Code.ShouldBe(WithdrawalErrors.AmountNotPositive.Code);

    [Fact]
    public void Approve_moves_a_pending_withdrawal_forward()
    {
        var w = Approved(requiresApproval: true);
        w.Approve("ops@cpe", Now).IsSuccess.ShouldBeTrue();
        w.Status.ShouldBe(WithdrawalStatus.Approved);
        w.ApprovedBy.ShouldBe("ops@cpe");
    }

    [Fact]
    public void Approving_an_already_approved_withdrawal_is_refused() =>
        Approved(requiresApproval: false).Approve("ops", Now).Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);

    [Fact]
    public void Reject_returns_the_reserved_funds()
    {
        var w = Approved(requiresApproval: true);

        w.Reject("ops", "suspicious", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.Rejected);
        var evt = w.DomainEvents.OfType<WithdrawalFailed>().ShouldHaveSingleItem();
        evt.WithdrawalId.ShouldBe(w.Id);
        evt.AmountBaseUnits.ShouldBe(Amount.ToString());
        evt.FeeBaseUnits.ShouldBe(Fee.ToString());
    }

    [Fact]
    public void The_happy_path_confirms_and_raises_settlement()
    {
        var w = Approved(requiresApproval: false);
        var signingId = Guid.CreateVersion7();

        w.BeginSigning(signingId, Now).IsSuccess.ShouldBeTrue();
        w.Status.ShouldBe(WithdrawalStatus.Signing);
        w.SigningRequestId.ShouldBe(signingId);

        w.MarkBroadcast("0xtxhash", Now).IsSuccess.ShouldBeTrue();
        w.Status.ShouldBe(WithdrawalStatus.Broadcast);
        w.TransactionHash.ShouldBe("0xtxhash");

        w.Confirm(Now).IsSuccess.ShouldBeTrue();
        w.Status.ShouldBe(WithdrawalStatus.Confirmed);

        var evt = w.DomainEvents.OfType<WithdrawalConfirmed>().ShouldHaveSingleItem();
        evt.TransactionHash.ShouldBe("0xtxhash");
        evt.AmountBaseUnits.ShouldBe(Amount.ToString());
        evt.FeeBaseUnits.ShouldBe(Fee.ToString());
    }

    [Fact]
    public void Fail_before_broadcast_releases_the_funds()
    {
        var w = Approved(requiresApproval: false);
        w.BeginSigning(Guid.CreateVersion7(), Now);

        w.Fail("signer unavailable", Now).IsSuccess.ShouldBeTrue();

        w.Status.ShouldBe(WithdrawalStatus.Failed);
        w.DomainEvents.OfType<WithdrawalFailed>().ShouldHaveSingleItem().Reason.ShouldBe("signer unavailable");
    }

    [Fact]
    public void Fail_after_broadcast_is_refused_because_funds_may_be_on_chain()
    {
        var w = Approved(requiresApproval: false);
        w.BeginSigning(Guid.CreateVersion7(), Now);
        w.MarkBroadcast("0xtx", Now);

        w.Fail("timeout", Now).Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);
        w.Status.ShouldBe(WithdrawalStatus.Broadcast); // unchanged — an ops incident, not an auto-release
        w.DomainEvents.OfType<WithdrawalFailed>().ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_order_transitions_are_refused()
    {
        var w = Approved(requiresApproval: false);
        w.MarkBroadcast("0xtx", Now).Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code); // can't broadcast before signing
        w.Confirm(Now).Error!.Code.ShouldBe(WithdrawalErrors.InvalidStateTransition.Code);                 // can't confirm before broadcast
    }
}
