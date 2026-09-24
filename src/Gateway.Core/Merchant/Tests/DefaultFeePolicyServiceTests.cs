using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// <see cref="DefaultFeePolicyService"/> — a plain per-asset store for the create-merchant screen's fee
/// pre-fill. No aggregate, no runtime charge-time behavior; validation is delegated to the domain
/// <see cref="FeeSchedule"/>, same as the per-merchant fee service.
/// </summary>
public sealed class DefaultFeePolicyServiceTests
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IDefaultFeePolicyService Service, FakeRepo Repo) Compose()
    {
        var repo = new FakeRepo();
        return (new DefaultFeePolicyService(repo, new FakeClock()), repo);
    }

    [Fact]
    public async Task Setting_a_default_for_a_new_asset_creates_it()
    {
        var (service, repo) = Compose();

        var result = await service.SetAsync(
            Asset, new BigInteger(0), 200, new BigInteger(500_000),
            new BigInteger(1_000_000), 100, new BigInteger(1_000_000), Ct);

        result.IsSuccess.ShouldBeTrue();
        repo.Saves.ShouldBe(1);

        var list = (await service.ListAsync(Ct)).Value;
        var view = list.ShouldHaveSingleItem();
        view.AssetId.ShouldBe(Asset);
        view.DepositFeeFixed.ShouldBe("0");
        view.DepositFeeBps.ShouldBe(200);
        view.MinimumDepositFee.ShouldBe("500000");
        view.WithdrawalFee.ShouldBe("1000000");
        view.WithdrawalFeeBps.ShouldBe(100);
        view.MinimumWithdrawalFee.ShouldBe("1000000");
    }

    [Fact]
    public async Task Setting_a_default_for_an_existing_asset_replaces_it_in_place()
    {
        var (service, repo) = Compose();
        await service.SetAsync(Asset, new BigInteger(0), 200, BigInteger.Zero, new BigInteger(0), 100, BigInteger.Zero, Ct);

        var second = await service.SetAsync(
            Asset, new BigInteger(1), 300, BigInteger.Zero, new BigInteger(2), 150, BigInteger.Zero, Ct);

        second.IsSuccess.ShouldBeTrue();
        repo.Saves.ShouldBe(2);

        // Still exactly one row for this asset — the second call updated it, not added a duplicate.
        var list = (await service.ListAsync(Ct)).Value;
        var view = list.ShouldHaveSingleItem();
        view.DepositFeeFixed.ShouldBe("1");
        view.DepositFeeBps.ShouldBe(300);
        view.WithdrawalFee.ShouldBe("2");
        view.WithdrawalFeeBps.ShouldBe(150);
    }

    [Fact]
    public async Task An_out_of_range_percent_is_rejected()
    {
        var (service, _) = Compose();

        var result = await service.SetAsync(
            Asset, BigInteger.Zero, FeeSchedule.MaxBps + 1, BigInteger.Zero,
            BigInteger.Zero, 0, BigInteger.Zero, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.FeeBpsInvalid.Code);
    }

    [Fact]
    public async Task A_negative_fixed_fee_is_rejected()
    {
        var (service, _) = Compose();

        var result = await service.SetAsync(
            Asset, new BigInteger(-1), 0, BigInteger.Zero, BigInteger.Zero, 0, BigInteger.Zero, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.AmountNegative.Code);
    }

    [Fact]
    public async Task No_defaults_configured_returns_an_empty_list_not_an_error()
    {
        var (service, _) = Compose();

        var result = await service.ListAsync(Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeRepo : IDefaultFeePolicyRepository
    {
        private readonly List<DefaultFeePolicy> _policies = [];
        public int Saves { get; private set; }

        public Task<IReadOnlyList<DefaultFeePolicy>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DefaultFeePolicy>>([.. _policies]);

        public Task<DefaultFeePolicy?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_policies.SingleOrDefault(p => p.AssetId == assetId));

        public void Add(DefaultFeePolicy policy) => _policies.Add(policy);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(1);
        }
    }
}
