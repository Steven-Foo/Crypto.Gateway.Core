using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Tests;

/// <summary>
/// The scanner turns "a deposit address holds enough to be worth sweeping" into a Pending sweep into a cold
/// collection wallet. Money-relevant: it sweeps only at/above the threshold (never dust), never when one is
/// already in flight, never at all without a registered destination — and, once screening is on, it sends a
/// flagged address's balance to the quarantine wallet rather than mixing it into clean treasury.
/// </summary>
public sealed class SweepScanServiceTests
{
    private const string SafeWallet = "TColdTreasury";
    private const string DangerWallet = "TColdQuarantine";
    private const string Deposit1 = "TDeposit1";
    private const string Deposit2 = "TDeposit2";
    private static readonly Guid Usdt = Guid.CreateVersion7();
    private static readonly Guid Wallet1 = Guid.CreateVersion7();
    private static readonly Guid Wallet2 = Guid.CreateVersion7();
    private static readonly Guid SafeWalletId = Guid.CreateVersion7();
    private static readonly Guid DangerWalletId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAssetCatalog _assets = Substitute.For<IAssetCatalog>();
    private readonly IWalletDirectory _wallets = Substitute.For<IWalletDirectory>();
    private readonly InMemoryBalanceReader _balances = new();
    private readonly ITreasuryColdWalletDirectory _coldWallets = Substitute.For<ITreasuryColdWalletDirectory>();
    private readonly ISweepPolicyProvider _policies = Substitute.For<ISweepPolicyProvider>();
    private readonly ISweepRepository _repository = Substitute.For<ISweepRepository>();
    private readonly IAddressScreeningService _screening = Substitute.For<IAddressScreeningService>();
    private readonly SweepScreeningOptions _screeningOptions = new();

    public SweepScanServiceTests()
    {
        _assets.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new AssetDto(Usdt, Chain.Tron, "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6, IsNative: false)]);
        _wallets.ListReceivingDepositAddressesAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns([new ReceivingDepositAddress(Wallet1, Deposit1), new ReceivingDepositAddress(Wallet2, Deposit2)]);
        _coldWallets.GetAsync(Chain.Tron, ColdWalletKind.Safe, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ColdTreasuryWallet(SafeWalletId, Chain.Tron, ColdWalletKind.Safe, SafeWallet)));
        _coldWallets.GetAsync(Chain.Tron, ColdWalletKind.Danger, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ColdTreasuryWallet(DangerWalletId, Chain.Tron, ColdWalletKind.Danger, DangerWallet)));
        _policies.ForAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(new SweepPolicy(1_000_000, 19)); // 1 USDT
        _repository.HasInFlightAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _repository.TryAddAsync(Arg.Any<Domain.Sweep>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private SweepScanService Service => new(
        _assets, _wallets, _balances, _coldWallets, _policies, _repository, TimeProvider.System,
        Options.Create(_screeningOptions), NullLogger<SweepScanService>.Instance, _screening);

    [Fact]
    public async Task Only_addresses_at_or_above_the_threshold_are_swept_and_the_full_balance_goes_to_the_cold_treasury()
    {
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000); // >= threshold
        _balances.Set(Chain.Tron, Deposit2, Usdt, 500_000);   // dust, below threshold

        Domain.Sweep? created = null;
        _repository.TryAddAsync(Arg.Do<Domain.Sweep>(s => created = s), Arg.Any<CancellationToken>()).Returns(true);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(1);
        created.ShouldNotBeNull();
        created.FromAddress.ShouldBe(Deposit1);
        created.ToAddress.ShouldBe(SafeWallet);
        created.Amount.ShouldBe(new BigInteger(5_000_000)); // the full balance is swept
        created.WalletId.ShouldBe(Wallet1);
        created.DestinationKind.ShouldBe(SweepDestinationKind.Safe);
    }

    [Fact]
    public async Task An_address_with_a_sweep_already_in_flight_is_skipped()
    {
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        _balances.Set(Chain.Tron, Deposit2, Usdt, 5_000_000);
        _repository.HasInFlightAsync(Wallet1, Usdt, Arg.Any<CancellationToken>()).Returns(true); // one already moving

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(1); // only Deposit2
        await _repository.DidNotReceive().TryAddAsync(
            Arg.Is<Domain.Sweep>(s => s.FromAddress == Deposit1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_swept_when_no_cold_treasury_destination_is_registered()
    {
        _coldWallets.GetAsync(Chain.Tron, ColdWalletKind.Safe, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ColdTreasuryWallet>(Error.NotFound("treasury.none", "no cold treasury")));
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(0);
        await _repository.DidNotReceive().TryAddAsync(Arg.Any<Domain.Sweep>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The opt-in guard: with screening off, nothing is asked of the provider and every sweep goes
    /// where it always went. Passing is evidence, not the absence of it — the stub throws if reached.</summary>
    [Fact]
    public async Task With_screening_off_the_provider_is_never_called()
    {
        _screening.FindAddressesNeedingScreeningAsync(
                Arg.Any<Chain>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("screening must not be consulted when it is switched off"));
        _screening.FindLatestAsync(Arg.Any<Chain>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("screening must not be consulted when it is switched off"));

        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(1);
    }

    [Fact]
    public async Task A_clean_deposit_address_is_swept_into_the_safe_collection_wallet()
    {
        EnableScreening();
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        StoredVerdict(Deposit1, ScreeningDecision.Allow);

        var created = await CaptureAsync();

        created.ShouldNotBeNull();
        created.ToAddress.ShouldBe(SafeWallet);
        created.DestinationKind.ShouldBe(SweepDestinationKind.Safe);
        created.ScreeningDecision.ShouldBe("Allow");
    }

    [Theory]
    [InlineData(ScreeningDecision.Block)]
    [InlineData(ScreeningDecision.Review)]
    public async Task A_flagged_deposit_address_is_swept_into_the_danger_collection_wallet(ScreeningDecision decision)
    {
        EnableScreening();
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        StoredVerdict(Deposit1, decision);

        var created = await CaptureAsync();

        created.ShouldNotBeNull();
        created.ToAddress.ShouldBe(DangerWallet);
        created.DestinationKind.ShouldBe(SweepDestinationKind.Danger);
        created.ScreeningDecision.ShouldBe(decision.ToString());
    }

    /// <summary>The rule that makes segregation worth having: with nowhere quarantined to put flagged funds,
    /// they stay on the deposit address. Falling back to the clean wallet would mix them irreversibly.</summary>
    [Fact]
    public async Task A_flagged_address_is_held_rather_than_swept_into_the_safe_wallet_when_no_danger_wallet_exists()
    {
        EnableScreening();
        _coldWallets.GetAsync(Chain.Tron, ColdWalletKind.Danger, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ColdTreasuryWallet>(Error.NotFound("treasury.none", "no quarantine wallet")));
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        StoredVerdict(Deposit1, ScreeningDecision.Block);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(0);
        await _repository.DidNotReceive().TryAddAsync(Arg.Any<Domain.Sweep>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_address_with_no_verdict_is_held_by_default()
    {
        EnableScreening();
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        _screening.FindLatestAsync(Chain.Tron, Deposit1, Arg.Any<CancellationToken>()).Returns((ScreeningVerdict?)null);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task An_address_with_no_verdict_follows_the_configured_fallback_when_it_is_not_hold()
    {
        EnableScreening();
        _screeningOptions.OnUnavailable = UnscreenedSweepAction.Danger;
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        _screening.FindLatestAsync(Chain.Tron, Deposit1, Arg.Any<CancellationToken>()).Returns((ScreeningVerdict?)null);

        var created = await CaptureAsync();

        created.ShouldNotBeNull();
        created.ToAddress.ShouldBe(DangerWallet);
    }

    /// <summary>The budget is what keeps a growing address set from spending the quota the payout gate
    /// depends on: only the addresses the pass took are screened live, and the rest wait for the next one.</summary>
    [Fact]
    public async Task Only_addresses_within_the_per_pass_budget_are_screened_live()
    {
        EnableScreening();
        _screeningOptions.MaxScreeningsPerPass = 1;
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        _balances.Set(Chain.Tron, Deposit2, Usdt, 5_000_000);

        // The budget query answers with one address; the other is served from stored evidence.
        _screening.FindAddressesNeedingScreeningAsync(
                Chain.Tron, Arg.Any<IReadOnlyCollection<string>>(), 1, Arg.Any<CancellationToken>())
            .Returns([Deposit1]);
        _screening.ScreenAsync(Chain.Tron, Deposit1, ScreeningPurpose.DepositAddress, Arg.Any<CancellationToken>())
            .Returns(Verdict(ScreeningDecision.Allow));
        StoredVerdict(Deposit2, ScreeningDecision.Allow);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(2);
        await _screening.Received(1).ScreenAsync(
            Chain.Tron, Deposit1, ScreeningPurpose.DepositAddress, Arg.Any<CancellationToken>());
        await _screening.DidNotReceive().ScreenAsync(
            Chain.Tron, Deposit2, ScreeningPurpose.DepositAddress, Arg.Any<CancellationToken>());
    }

    private void EnableScreening()
    {
        _screeningOptions.Enabled = true;
        _screening.FindAddressesNeedingScreeningAsync(
                Arg.Any<Chain>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private void StoredVerdict(string address, ScreeningDecision decision) =>
        _screening.FindLatestAsync(Chain.Tron, address, Arg.Any<CancellationToken>()).Returns(Verdict(decision));

    private static ScreeningVerdict Verdict(ScreeningDecision decision) => new(
        Guid.CreateVersion7(), decision, Score: decision == ScreeningDecision.Allow ? 2 : 80,
        RiskLevel: null, Reasons: [], AddressLabel: null, ReportUrl: null,
        ScreenedAt: DateTimeOffset.UtcNow, FromCache: true);

    private async Task<Domain.Sweep?> CaptureAsync()
    {
        Domain.Sweep? created = null;
        _repository.TryAddAsync(Arg.Do<Domain.Sweep>(s => created = s), Arg.Any<CancellationToken>()).Returns(true);
        await Service.ScanAsync(Chain.Tron, Ct);
        return created;
    }
}
