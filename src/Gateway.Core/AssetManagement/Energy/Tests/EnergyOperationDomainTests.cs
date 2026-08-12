using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The 5b energy-operation state machine (stake/delegate). Same money-safety shape as sweep/withdrawal: the
/// signed blob is recorded before broadcast, failure is only reachable pre-broadcast, transitions are guarded.
/// </summary>
public sealed class EnergyOperationDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Guid Wallet = Guid.CreateVersion7();
    private static readonly byte[] Signed = [1, 2, 3];

    [Fact]
    public void A_stake_starts_pending_with_no_target()
    {
        var op = EnergyOperation.CreateStake(Wallet, Chain.Tron, "TStaker", 100_000_000, Now).Value;
        op.Kind.ShouldBe(EnergyOperationKind.Stake);
        op.Status.ShouldBe(EnergyOperationStatus.Pending);
        op.TargetAddress.ShouldBeNull();
        op.AmountSun.ShouldBe(new BigInteger(100_000_000));
    }

    [Fact]
    public void A_delegate_requires_a_receiver()
    {
        EnergyOperation.CreateDelegate(Wallet, Chain.Tron, "TStaker", "", 1, Now)
            .Error!.Code.ShouldBe(EnergyErrors.DelegateReceiverRequired.Code);

        var ok = EnergyOperation.CreateDelegate(Wallet, Chain.Tron, "TStaker", "TDeposit", 20_000_000, Now).Value;
        ok.Kind.ShouldBe(EnergyOperationKind.Delegate);
        ok.TargetAddress.ShouldBe("TDeposit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_amount_is_refused(long amount) =>
        EnergyOperation.CreateStake(Wallet, Chain.Tron, "TStaker", amount, Now)
            .Error!.Code.ShouldBe(EnergyErrors.OperationAmountNotPositive.Code);

    [Fact]
    public void The_happy_path_walks_pending_to_confirmed()
    {
        var op = EnergyOperation.CreateDelegate(Wallet, Chain.Tron, "TStaker", "TDeposit", 20_000_000, Now).Value;

        op.RecordSigned(Guid.CreateVersion7(), Signed, Now).IsSuccess.ShouldBeTrue();
        op.Status.ShouldBe(EnergyOperationStatus.Signing);
        op.MarkBroadcast("0xhash", Now).IsSuccess.ShouldBeTrue();
        op.Status.ShouldBe(EnergyOperationStatus.Broadcast);
        op.RecordConfirmations(19, Now).IsSuccess.ShouldBeTrue();
        op.Confirm(Now).IsSuccess.ShouldBeTrue();
        op.Status.ShouldBe(EnergyOperationStatus.Confirmed);
    }

    [Fact]
    public void A_broadcast_operation_cannot_be_failed()
    {
        var op = EnergyOperation.CreateStake(Wallet, Chain.Tron, "TStaker", 100_000_000, Now).Value;
        op.RecordSigned(Guid.CreateVersion7(), Signed, Now);
        op.MarkBroadcast("0xhash", Now);

        op.Fail("late", Now).Error!.Code.ShouldBe(EnergyErrors.OperationInvalidStateTransition.Code);
        op.Status.ShouldBe(EnergyOperationStatus.Broadcast);
    }

    [Fact]
    public void Transitions_are_guarded_out_of_order()
    {
        var op = EnergyOperation.CreateStake(Wallet, Chain.Tron, "TStaker", 100_000_000, Now).Value;
        op.MarkBroadcast("0xhash", Now).Error!.Code.ShouldBe(EnergyErrors.OperationInvalidStateTransition.Code);
        op.Confirm(Now).Error!.Code.ShouldBe(EnergyErrors.OperationInvalidStateTransition.Code);
    }
}
