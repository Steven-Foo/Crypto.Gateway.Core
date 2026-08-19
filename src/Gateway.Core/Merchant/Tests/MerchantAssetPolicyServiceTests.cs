using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// The write path that lets staff actually price a merchant (until it existed, every merchant was unpriced ⇒
/// zero fee). Pure unit tests over a fake repository: validation is delegated to the domain FeeSchedule, and
/// v1 sets pricing only — existing operational limits must survive a fee change untouched.
/// </summary>
public sealed class MerchantAssetPolicyServiceTests
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MerchantEntity ActiveMerchant()
    {
        var merchant = MerchantEntity.Create("ACME", "Acme", null).Value;
        merchant.Activate(Now);
        return merchant;
    }

    private static (MerchantAssetPolicyService Service, FakeRepo Repo) Compose(MerchantEntity merchant)
    {
        var repo = new FakeRepo(merchant);
        return (new MerchantAssetPolicyService(repo, new FakeClock()), repo);
    }

    [Fact]
    public async Task Setting_a_fee_for_a_new_asset_prices_it_with_unset_limits()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        var result = await service.SetFeesAsync(merchant.Id, Asset, new BigInteger(5), 100, new BigInteger(3), 50, Ct);

        result.IsSuccess.ShouldBeTrue();
        repo.Saves.ShouldBe(1);

        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.DepositFeeFixed.ShouldBe(new BigInteger(5));
        policy.DepositFeeBps.ShouldBe(100);
        policy.WithdrawalFee.ShouldBe(new BigInteger(3));
        policy.WithdrawalFeeBps.ShouldBe(50);
        policy.SweepThreshold.ShouldBe(BigInteger.Zero);   // benign default
        policy.MinimumWithdrawal.ShouldBeNull();           // limits unset ⇒ the flow uses the config default
        policy.MaximumWithdrawal.ShouldBeNull();
    }

    [Fact]
    public async Task Setting_withdrawal_limits_persists_and_is_listed()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        var result = await service.SetWithdrawalLimitsAsync(
            merchant.Id, Asset, new BigInteger(1_000), new BigInteger(5_000_000), Ct);

        result.IsSuccess.ShouldBeTrue();
        repo.Saves.ShouldBe(1);
        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.MinimumWithdrawal.ShouldBe(new BigInteger(1_000));
        policy.MaximumWithdrawal.ShouldBe(new BigInteger(5_000_000));

        var view = (await service.ListAsync(merchant.Id, Ct)).Value.ShouldHaveSingleItem();
        view.MinimumWithdrawal.ShouldBe("1000");
        view.MaximumWithdrawal.ShouldBe("5000000");
    }

    [Fact]
    public async Task Setting_limits_preserves_an_existing_fee_and_null_leaves_a_bound_unset()
    {
        var merchant = ActiveMerchant();
        var (service, _) = Compose(merchant);
        await service.SetFeesAsync(merchant.Id, Asset, new BigInteger(5), 100, new BigInteger(3), 50, Ct);

        // Set only a maximum; minimum stays unset (null ⇒ config default).
        await service.SetWithdrawalLimitsAsync(merchant.Id, Asset, null, new BigInteger(9_000), Ct);

        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.DepositFeeBps.ShouldBe(100);                 // fee untouched by a limits change
        policy.WithdrawalFeeBps.ShouldBe(50);
        policy.MinimumWithdrawal.ShouldBeNull();
        policy.MaximumWithdrawal.ShouldBe(new BigInteger(9_000));
    }

    [Fact]
    public async Task A_minimum_above_the_maximum_is_rejected_by_the_domain()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        var result = await service.SetWithdrawalLimitsAsync(
            merchant.Id, Asset, new BigInteger(1_000), new BigInteger(100), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.WithdrawalRangeInvalid.Code);
        repo.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Setting_a_fee_preserves_existing_operational_limits()
    {
        var merchant = ActiveMerchant();
        // Pre-existing policy with real limits (as a future limits screen would set).
        merchant.SetAssetPolicy(Asset, new BigInteger(100), new BigInteger(10), new BigInteger(1000), FeeSchedule.None, Now);
        var (service, _) = Compose(merchant);

        var result = await service.SetFeesAsync(merchant.Id, Asset, new BigInteger(7), 200, new BigInteger(4), 25, Ct);

        result.IsSuccess.ShouldBeTrue();
        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.DepositFeeBps.ShouldBe(200);                     // fee updated
        policy.WithdrawalFeeBps.ShouldBe(25);
        policy.SweepThreshold.ShouldBe(new BigInteger(100));    // limits untouched
        policy.MinimumWithdrawal.ShouldBe(new BigInteger(10));
        policy.MaximumWithdrawal.ShouldBe(new BigInteger(1000));
    }

    [Fact]
    public async Task An_invalid_bps_is_rejected_by_the_domain_and_nothing_is_saved()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        // 10000 bps = 100% deposit fee — unsolvable gross-up, rejected by FeeSchedule.Create.
        var result = await service.SetFeesAsync(merchant.Id, Asset, BigInteger.Zero, 10_000, BigInteger.Zero, 0, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.FeeBpsInvalid.Code);
        repo.Saves.ShouldBe(0);
        merchant.AssetPolicies.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_merchant_is_not_found()
    {
        var (service, _) = Compose(ActiveMerchant());

        var result = await service.SetFeesAsync(Guid.CreateVersion7(), Asset, BigInteger.Zero, 100, BigInteger.Zero, 0, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.NotFound.Code);
    }

    [Fact]
    public async Task A_closed_merchant_cannot_be_priced()
    {
        var merchant = ActiveMerchant();
        merchant.Close(Now);
        var (service, repo) = Compose(merchant);

        var result = await service.SetFeesAsync(merchant.Id, Asset, BigInteger.Zero, 100, BigInteger.Zero, 0, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.Closed.Code);
        repo.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task List_returns_the_priced_assets_as_base_unit_strings()
    {
        var merchant = ActiveMerchant();
        var (service, _) = Compose(merchant);
        await service.SetFeesAsync(merchant.Id, Asset, new BigInteger(5), 100, new BigInteger(3), 50, Ct);

        var list = await service.ListAsync(merchant.Id, Ct);

        list.IsSuccess.ShouldBeTrue();
        var view = list.Value.ShouldHaveSingleItem();
        view.AssetId.ShouldBe(Asset);
        view.DepositFeeFixed.ShouldBe("5");
        view.DepositFeeBps.ShouldBe(100);
        view.WithdrawalFee.ShouldBe("3");
        view.WithdrawalFeeBps.ShouldBe(50);
    }

    [Fact]
    public async Task Setting_a_cash_out_cap_persists_and_is_listed()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        var result = await service.SetMerchantWithdrawalCapAsync(merchant.Id, Asset, new BigInteger(1_000_000), 5000, Ct);

        result.IsSuccess.ShouldBeTrue();
        repo.Saves.ShouldBe(1);
        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.MerchantWithdrawalFlatCap.ShouldBe(new BigInteger(1_000_000));
        policy.MerchantWithdrawalPercentBps.ShouldBe(5000);

        var view = (await service.ListAsync(merchant.Id, Ct)).Value.ShouldHaveSingleItem();
        view.MerchantWithdrawalFlatCap.ShouldBe("1000000");
        view.MerchantWithdrawalPercentBps.ShouldBe(5000);
    }

    [Fact]
    public async Task A_percent_only_cap_leaves_the_flat_cap_null()
    {
        var merchant = ActiveMerchant();
        var (service, _) = Compose(merchant);

        await service.SetMerchantWithdrawalCapAsync(merchant.Id, Asset, null, 2500, Ct);

        var view = (await service.ListAsync(merchant.Id, Ct)).Value.ShouldHaveSingleItem();
        view.MerchantWithdrawalFlatCap.ShouldBeNull();
        view.MerchantWithdrawalPercentBps.ShouldBe(2500);
    }

    [Fact]
    public async Task Setting_a_cap_preserves_an_existing_fee()
    {
        var merchant = ActiveMerchant();
        var (service, _) = Compose(merchant);
        await service.SetFeesAsync(merchant.Id, Asset, new BigInteger(5), 100, new BigInteger(3), 50, Ct);

        await service.SetMerchantWithdrawalCapAsync(merchant.Id, Asset, null, 5000, Ct);

        var policy = merchant.AssetPolicies.ShouldHaveSingleItem();
        policy.DepositFeeBps.ShouldBe(100);                 // fee untouched by a cap change
        policy.WithdrawalFeeBps.ShouldBe(50);
        policy.MerchantWithdrawalPercentBps.ShouldBe(5000); // cap applied
    }

    [Fact]
    public async Task An_out_of_range_cap_bps_is_rejected_by_the_domain()
    {
        var merchant = ActiveMerchant();
        var (service, repo) = Compose(merchant);

        var result = await service.SetMerchantWithdrawalCapAsync(merchant.Id, Asset, null, 10_001, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.WithdrawalCapBpsInvalid.Code);
        repo.Saves.ShouldBe(0);
    }

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Holds one merchant by id; only <see cref="GetByIdAsync"/> and <see cref="SaveChangesAsync"/>
    /// are exercised by the service.</summary>
    private sealed class FakeRepo(MerchantEntity merchant) : IMerchantRepository
    {
        public int Saves { get; private set; }

        public Task<MerchantEntity?> GetByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(merchant.Id == merchantId ? merchant : null);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<MerchantEntity?> GetByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<(IReadOnlyList<MerchantEntity> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAllAllowedIpsExceptAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<MerchantApiCredential?> FindActiveCredentialAsync(string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<MerchantApiCredential?> FindActiveCredentialByMerchantAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Add(MerchantEntity merchant) => throw new NotSupportedException();
    }
}
