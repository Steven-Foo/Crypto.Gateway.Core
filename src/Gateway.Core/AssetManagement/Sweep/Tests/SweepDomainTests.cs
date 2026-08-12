using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Tests;

/// <summary>
/// The sweep state machine. Money-safety mirrors withdrawal: the signed blob is recorded before broadcast, a
/// failure is only reachable pre-broadcast (nothing left the chain), and every transition is guarded.
/// </summary>
public sealed class SweepDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly byte[] Signed = [1, 2, 3, 4];

    private static Domain.Sweep NewSweep(BigInteger? amount = null) =>
        Domain.Sweep.Create(Guid.CreateVersion7(), Chain.Tron, Guid.CreateVersion7(), "TFrom", "THot", amount ?? 5_000_000, Now).Value;

    [Fact]
    public void Create_starts_pending_with_the_full_balance()
    {
        var sweep = NewSweep();
        sweep.Status.ShouldBe(SweepStatus.Pending);
        sweep.Amount.ShouldBe(new BigInteger(5_000_000));
        sweep.FromAddress.ShouldBe("TFrom");
        sweep.ToAddress.ShouldBe("THot");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_refuses_a_non_positive_amount(long amount) =>
        Domain.Sweep.Create(Guid.CreateVersion7(), Chain.Tron, Guid.CreateVersion7(), "TFrom", "THot", amount, Now)
            .Error!.Code.ShouldBe(SweepErrors.AmountNotPositive.Code);

    [Fact]
    public void The_happy_path_walks_pending_to_confirmed()
    {
        var sweep = NewSweep();

        sweep.RecordSigned(Guid.CreateVersion7(), Signed, Now).IsSuccess.ShouldBeTrue();
        sweep.Status.ShouldBe(SweepStatus.Signing);
        sweep.HasSignedTransaction.ShouldBeTrue();

        sweep.MarkBroadcast("0xhash", Now).IsSuccess.ShouldBeTrue();
        sweep.Status.ShouldBe(SweepStatus.Broadcast);
        sweep.TransactionHash.ShouldBe("0xhash");

        sweep.RecordConfirmations(19, Now).IsSuccess.ShouldBeTrue();
        sweep.Confirm(Now).IsSuccess.ShouldBeTrue();
        sweep.Status.ShouldBe(SweepStatus.Confirmed);
    }

    [Fact]
    public void Signing_requires_a_non_empty_blob()
    {
        var sweep = NewSweep();
        sweep.RecordSigned(Guid.CreateVersion7(), [], Now).IsFailure.ShouldBeTrue();
        sweep.Status.ShouldBe(SweepStatus.Pending);
    }

    [Fact]
    public void A_pre_broadcast_sweep_can_fail_safely()
    {
        var sweep = NewSweep();
        sweep.Fail("no signing key", Now).IsSuccess.ShouldBeTrue();
        sweep.Status.ShouldBe(SweepStatus.Failed);
        sweep.FailureReason.ShouldBe("no signing key");
    }

    [Fact]
    public void A_broadcast_sweep_cannot_be_failed_because_funds_may_be_on_chain()
    {
        var sweep = NewSweep();
        sweep.RecordSigned(Guid.CreateVersion7(), Signed, Now);
        sweep.MarkBroadcast("0xhash", Now);

        // Once broadcast, Fail is refused — it would be an ops incident, not an automatic release.
        sweep.Fail("late", Now).Error!.Code.ShouldBe(SweepErrors.InvalidStateTransition.Code);
        sweep.Status.ShouldBe(SweepStatus.Broadcast);
    }

    [Fact]
    public void Transitions_are_guarded_against_out_of_order_calls()
    {
        var sweep = NewSweep();

        // Can't broadcast before signing, can't confirm before broadcast.
        sweep.MarkBroadcast("0xhash", Now).Error!.Code.ShouldBe(SweepErrors.InvalidStateTransition.Code);
        sweep.Confirm(Now).Error!.Code.ShouldBe(SweepErrors.InvalidStateTransition.Code);
        sweep.RecordConfirmations(5, Now).Error!.Code.ShouldBe(SweepErrors.InvalidStateTransition.Code);
    }
}
