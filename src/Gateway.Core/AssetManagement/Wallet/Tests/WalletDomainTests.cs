using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using WalletEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain.Wallet;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Tests;

public sealed class WalletDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid DerivedKeyId = Guid.CreateVersion7();
    private static readonly Guid MerchantId = Guid.CreateVersion7();

    [Fact]
    public void Create_deposit_seeds_an_active_assignment_to_the_merchant()
    {
        var wallet = WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "TDeposit1", MerchantId).Value;

        wallet.WalletType.ShouldBe(WalletType.Deposit);
        wallet.MerchantId.ShouldBe(MerchantId);
        wallet.IsMerchantAssignable.ShouldBeTrue();
        wallet.Assignments.Count.ShouldBe(1);
        wallet.ActiveAssignment.ShouldNotBeNull();
        wallet.ActiveAssignment.MerchantId.ShouldBe(MerchantId);
        wallet.DerivedKeyId.ShouldBe(DerivedKeyId);
    }

    [Fact]
    public void Create_deposit_requires_a_merchant() =>
        WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "T", Guid.Empty)
            .Error!.Code.ShouldBe(WalletErrors.MerchantRequiredForDeposit.Code);

    [Fact]
    public void Create_deposit_requires_a_derived_key() =>
        WalletEntity.CreateDeposit(Guid.Empty, Chain.Tron, "T", MerchantId)
            .Error!.Code.ShouldBe(WalletErrors.DerivedKeyRequired.Code);

    [Fact]
    public void Create_deposit_requires_an_address() =>
        WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "  ", MerchantId)
            .Error!.Code.ShouldBe(WalletErrors.AddressRequired.Code);

    [Theory]
    [InlineData(WalletType.HotWithdrawal)]
    [InlineData(WalletType.Treasury)]
    [InlineData(WalletType.Cold)]
    [InlineData(WalletType.Energy)]
    public void Create_platform_has_no_merchant_and_no_assignment(WalletType type)
    {
        var wallet = WalletEntity.CreatePlatform(DerivedKeyId, Chain.Ethereum, "0xhot", type).Value;

        wallet.WalletType.ShouldBe(type);
        wallet.MerchantId.ShouldBeNull();
        wallet.IsMerchantAssignable.ShouldBeFalse();
        wallet.Assignments.ShouldBeEmpty();
        wallet.ActiveAssignment.ShouldBeNull();
    }

    [Fact]
    public void Create_platform_rejects_the_deposit_type() =>
        WalletEntity.CreatePlatform(DerivedKeyId, Chain.Tron, "T", WalletType.Deposit)
            .Error!.Code.ShouldBe(WalletErrors.MerchantRequiredForDeposit.Code);

    [Fact]
    public void Disable_releases_the_active_assignment_and_clears_the_holder()
    {
        var wallet = WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "TDeposit1", MerchantId).Value;

        wallet.Disable(Now);

        wallet.IsActive.ShouldBeFalse();
        wallet.MerchantId.ShouldBeNull();
        wallet.ActiveAssignment.ShouldBeNull();

        // The assignment is released, not deleted — a late deposit stays attributable to its history.
        wallet.Assignments.Count.ShouldBe(1);
        wallet.Assignments[0].Status.ShouldBe(WalletAssignmentStatus.Released);
        wallet.Assignments[0].ReleasedAt.ShouldBe(Now);
        wallet.Assignments[0].MerchantId.ShouldBe(MerchantId);
    }

    [Fact]
    public void Disable_is_idempotent()
    {
        var wallet = WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "T", MerchantId).Value;

        wallet.Disable(Now);
        Should.NotThrow(() => wallet.Disable(Now.AddMinutes(1)));
        wallet.Assignments.Count(a => a.Status == WalletAssignmentStatus.Released).ShouldBe(1);
    }

    [Fact]
    public void Release_assignment_keeps_the_wallet_active_but_drops_the_holder()
    {
        var wallet = WalletEntity.CreateDeposit(DerivedKeyId, Chain.Tron, "T", MerchantId).Value;

        wallet.ReleaseAssignment(Now).IsSuccess.ShouldBeTrue();

        wallet.IsActive.ShouldBeTrue();          // still receiving
        wallet.MerchantId.ShouldBeNull();
        wallet.ActiveAssignment.ShouldBeNull();
    }

    [Fact]
    public void Release_assignment_on_a_platform_wallet_fails()
    {
        var wallet = WalletEntity.CreatePlatform(DerivedKeyId, Chain.Tron, "T", WalletType.Treasury).Value;

        wallet.ReleaseAssignment(Now).Error!.Code.ShouldBe(WalletErrors.NotAssigned.Code);
    }
}
