using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>Step 5: sizing a lend from the network rate, taking lent energy back, and the staff unstake/withdraw.</summary>
public sealed class EnergyReclaimTests
{
    private const string Hub = "THub";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly EnergyOperationOptions Options = new();

    // ── Sizing ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_mainnet_like_rate_lends_thousands_of_trx_not_twenty()
    {
        // ~9.47 energy per staked TRX: 131,000 energy needs 13,828 TRX, plus 10% margin = 15,211 TRX.
        var sun = DelegationSizing.SunToLend(131_000, 180_000_000_000, 19_000_000_000, Options);
        sun.ShouldBe(new BigInteger(15_211) * 1_000_000);
    }

    [Fact]
    public void Only_the_shortfall_is_lent()
    {
        var full = DelegationSizing.SunToLend(131_000, 180_000_000_000, 19_000_000_000, Options);
        var half = DelegationSizing.SunToLend(65_500, 180_000_000_000, 19_000_000_000, Options);
        half.ShouldBeLessThan(full);
    }

    [Fact]
    public void An_unknown_rate_falls_back_to_the_configured_amount()
    {
        DelegationSizing.SunToLend(131_000, 0, 0, Options).ShouldBe(Options.DelegateTrxSun);
        DelegationSizing.SunToLend(131_000, 180_000_000_000, 0, Options).ShouldBe(Options.DelegateTrxSun);
    }

    [Fact]
    public void A_wild_rate_reading_is_capped()
    {
        // Energy nearly worthless per TRX: the formula asks for millions of TRX; the cap holds it.
        DelegationSizing.SunToLend(131_000, 1, 19_000_000_000, Options).ShouldBe(Options.MaxDelegateTrxSun);
    }

    [Fact]
    public void Never_less_than_one_trx_and_always_rounded_up()
    {
        // A cheap test network: 1 energy needs a fraction of a TRX. That rounds UP to 1 TRX, and the 10% margin
        // on 1 TRX rounds up again to 2 — every step rounds towards "enough", never "a little short".
        DelegationSizing.SunToLend(1, 180_000_000_000, 1, Options).ShouldBe(new BigInteger(2_000_000));

        var noMargin = new EnergyOperationOptions { DelegateMarginPercent = 0 };
        DelegationSizing.SunToLend(1, 180_000_000_000, 1, noMargin).ShouldBe(new BigInteger(1_000_000)); // the 1 TRX floor
    }

    // ── Take-back ───────────────────────────────────────────────────────────

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly IDelegatedResourceReader _lent = Substitute.For<IDelegatedResourceReader>();
    private readonly IEnergyOperationRepository _operations = Substitute.For<IEnergyOperationRepository>();
    private readonly List<EnergyOperation> _added = [];
    private static readonly Guid StakingWalletId = Guid.NewGuid();

    public EnergyReclaimTests()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(new List<PlatformWallet>
        {
            new(StakingWalletId, Chain.Tron, Hub, "Energy"),
            new(Guid.NewGuid(), Chain.Tron, "THot1", "HotWithdrawal"),
        });
        _operations.TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _added.Add(ci.Arg<EnergyOperation>()); return true; });
    }

    private EnergyReclaimService Reclaim => new(
        new StakingWalletLocator(_wallets), _wallets, _lent, _operations, Options, TimeProvider.System,
        NullLogger<EnergyReclaimService>.Instance);

    private void Candidates(params string[] targets) =>
        _operations.ListReclaimCandidatesAsync(Chain.Tron, StakingWalletId, Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(targets);

    [Fact]
    public async Task Takes_back_exactly_what_the_chain_says_is_lent()
    {
        Candidates("TDeposit1");
        _lent.GetDelegatedEnergySunAsync(Chain.Tron, Hub, "TDeposit1", Arg.Any<CancellationToken>())
            .Returns(new BigInteger(15_211_000_000));

        (await Reclaim.ReclaimOnceAsync(Chain.Tron, Ct)).ShouldBe(1);

        var op = _added.ShouldHaveSingleItem();
        op.Kind.ShouldBe(EnergyOperationKind.Undelegate);
        op.Status.ShouldBe(EnergyOperationStatus.Pending);
        op.OwnerAddress.ShouldBe(Hub);
        op.TargetAddress.ShouldBe("TDeposit1");
        op.AmountSun.ShouldBe(new BigInteger(15_211_000_000));
    }

    [Fact]
    public async Task Nothing_lent_on_chain_is_recorded_once_without_sending_anything()
    {
        Candidates("TDeposit2");
        _lent.GetDelegatedEnergySunAsync(Chain.Tron, Hub, "TDeposit2", Arg.Any<CancellationToken>()).Returns(BigInteger.Zero);

        (await Reclaim.ReclaimOnceAsync(Chain.Tron, Ct)).ShouldBe(0);

        var op = _added.ShouldHaveSingleItem();
        op.Kind.ShouldBe(EnergyOperationKind.Undelegate);
        op.Status.ShouldBe(EnergyOperationStatus.Confirmed); // a marker, never picked up by the processing worker
        op.AmountSun.ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task The_platforms_own_wallets_are_excluded_from_the_search()
    {
        Candidates();

        await Reclaim.ReclaimOnceAsync(Chain.Tron, Ct);

        await _operations.Received(1).ListReclaimCandidatesAsync(
            Chain.Tron, StakingWalletId, Arg.Any<DateTimeOffset>(),
            Arg.Is<IReadOnlyCollection<string>>(x => x.Contains("THot1") && x.Contains(Hub)),
            Options.ReclaimBatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_staking_wallet_nothing_happens()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(new List<PlatformWallet>());

        (await Reclaim.ReclaimOnceAsync(Chain.Tron, Ct)).ShouldBe(0);
        await _operations.DidNotReceiveWithAnyArgs().ListReclaimCandidatesAsync(default, default, default, default!, default, Ct);
    }

    // ── Staff unstake / withdraw ─────────────────────────────────────────────

    private readonly IEnergyPolicyRepository _policies = Substitute.For<IEnergyPolicyRepository>();

    private EnergyAdministrationService Admin()
    {
        var locks = Substitute.For<IEnergyProvisioningLock>();
        locks.AcquireAsync(Arg.Any<Chain>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        return new EnergyAdministrationService(
            new StakingWalletLocator(_wallets), _wallets, Substitute.For<IWalletDerivation>(),
            Substitute.For<IPlatformWalletRegistrar>(), _policies, _operations, locks, TimeProvider.System,
            NullLogger<EnergyAdministrationService>.Instance);
    }

    private void AutoStake(bool on) =>
        _policies.FindAsync(Chain.Tron, "Energy", Arg.Any<CancellationToken>())
            .Returns(EnergyPolicy.Create(Chain.Tron, "Energy", 0, 0, 0, 0, on, false).Value);

    [Fact]
    public async Task Unstake_is_refused_while_auto_stake_is_on()
    {
        AutoStake(on: true);

        var result = await Admin().RequestUnstakeAsync(Chain.Tron, 1_000_000_000, Ct);

        result.Error!.Code.ShouldBe(EnergyErrors.AutoStakeStillOn.Code);
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unstake_queues_one_operation_when_auto_stake_is_off()
    {
        AutoStake(on: false);

        var result = await Admin().RequestUnstakeAsync(Chain.Tron, 1_000_000_000, Ct);

        result.IsSuccess.ShouldBeTrue();
        var op = _added.ShouldHaveSingleItem();
        op.Kind.ShouldBe(EnergyOperationKind.Unstake);
        op.OwnerAddress.ShouldBe(Hub);
        op.AmountSun.ShouldBe(new BigInteger(1_000_000_000));
    }

    [Fact]
    public async Task A_second_unstake_while_one_is_in_progress_is_refused()
    {
        AutoStake(on: false);
        _operations.HasInFlightAsync(StakingWalletId, EnergyOperationKind.Unstake, Arg.Any<CancellationToken>()).Returns(true);

        (await Admin().RequestUnstakeAsync(Chain.Tron, 1_000_000, Ct)).Error!.Code
            .ShouldBe(EnergyErrors.OperationAlreadyInFlight.Code);
        _added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Withdraw_queues_one_operation_for_the_staking_wallet()
    {
        var result = await Admin().RequestWithdrawUnstakedAsync(Chain.Tron, Ct);

        result.IsSuccess.ShouldBeTrue();
        _added.ShouldHaveSingleItem().Kind.ShouldBe(EnergyOperationKind.WithdrawUnstaked);
    }

    [Fact]
    public async Task Unstake_without_a_staking_wallet_is_refused()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(new List<PlatformWallet>());

        (await Admin().RequestUnstakeAsync(Chain.Tron, 1_000_000, Ct)).Error!.Code
            .ShouldBe(EnergyErrors.StakingWalletNotFound.Code);
    }
}
