using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

public sealed class AccountDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();

    [Theory]
    [InlineData(AccountType.TreasuryAsset, NormalSide.Debit)]
    [InlineData(AccountType.NetworkFeeExpense, NormalSide.Debit)]
    [InlineData(AccountType.MerchantLiability, NormalSide.Credit)]
    [InlineData(AccountType.FeeRevenue, NormalSide.Credit)]
    public void Normal_side_is_derived_from_type_never_supplied(AccountType type, NormalSide expected) =>
        Account.NormalSideOf(type).ShouldBe(expected);

    [Fact]
    public void A_merchant_liability_account_requires_an_owner()
    {
        Account.Open(AccountType.MerchantLiability, OwnerType.Merchant, ownerId: null, Asset, Now)
            .Error!.Code.ShouldBe(LedgerErrors.MerchantAccountNeedsOwner.Code);

        var ok = Account.Open(AccountType.MerchantLiability, OwnerType.Merchant, Merchant, Asset, Now).Value;
        ok.NormalSide.ShouldBe(NormalSide.Credit);
        ok.OwnerId.ShouldBe(Merchant);
        ok.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void A_treasury_account_must_not_carry_an_owner()
    {
        Account.Open(AccountType.TreasuryAsset, OwnerType.Treasury, Merchant, Asset, Now)
            .Error!.Code.ShouldBe(LedgerErrors.PlatformAccountHasNoOwner.Code);

        var ok = Account.Open(AccountType.TreasuryAsset, OwnerType.Treasury, ownerId: null, Asset, Now).Value;
        ok.NormalSide.ShouldBe(NormalSide.Debit);
        ok.OwnerId.ShouldBeNull();
    }

    [Fact]
    public void An_account_requires_an_asset() =>
        Account.Open(AccountType.TreasuryAsset, OwnerType.Treasury, null, Guid.Empty, Now)
            .Error!.Code.ShouldBe(LedgerErrors.AssetRequired.Code);
}
