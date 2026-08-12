using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Tests;

/// <summary>
/// The reconciliation invariant: the ledger's <c>TreasuryAsset</c> holding must equal the summed on-chain
/// balance across every controlled address (platform wallets + funded deposit addresses). These prove the
/// classification is exact (base units, §14), never money is moved, and a partial read is never a false pass.
/// </summary>
public sealed class ReconciliationServiceTests
{
    private const string HotWallet = "THotWallet";
    private const string Deposit1 = "TDeposit1";
    private const string Deposit2 = "TDeposit2";
    private static readonly Guid Usdt = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ILedgerQuery _ledger = Substitute.For<ILedgerQuery>();
    private readonly IAssetCatalog _assets = Substitute.For<IAssetCatalog>();
    private readonly IPlatformWalletDirectory _platform = Substitute.For<IPlatformWalletDirectory>();
    private readonly IWalletDirectory _deposits = Substitute.For<IWalletDirectory>();
    private readonly CapturingStore _store = new();

    public ReconciliationServiceTests()
    {
        _assets.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new AssetDto(Usdt, Chain.Tron, "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6, IsNative: false)]);
    }

    private ReconciliationService Build(IBalanceReader balances, BigInteger tolerance = default) =>
        new(_ledger, balances, _assets, _platform, _deposits, _store, _store,
            new ReconciliationOptions { DriftTolerance = tolerance }, TimeProvider.System, NullLogger<ReconciliationService>.Instance);

    private void GivenControlledAddresses(params (Guid Id, string Address)[] deposits)
    {
        _platform.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns([new PlatformWallet(Guid.CreateVersion7(), Chain.Tron, HotWallet, "HotWithdrawal")]);
        _deposits.ListReceivingDepositAddressesAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(deposits.Select(d => new ReceivingDepositAddress(d.Id, d.Address)).ToList());
    }

    [Fact]
    public async Task A_ledger_holding_matching_the_summed_on_chain_balance_is_balanced()
    {
        _ledger.GetTreasuryHoldingAsync(Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(10_000_000));
        GivenControlledAddresses((Guid.CreateVersion7(), Deposit1), (Guid.CreateVersion7(), Deposit2));

        var reader = new InMemoryBalanceReader();
        reader.Set(Chain.Tron, HotWallet, Usdt, 4_000_000);
        reader.Set(Chain.Tron, Deposit1, Usdt, 3_000_000);
        reader.Set(Chain.Tron, Deposit2, Usdt, 3_000_000);

        await Build(reader).ReconcileAsync(Chain.Tron, Ct);

        var snap = _store.Latest.ShouldNotBeNull();
        snap.Status.ShouldBe(ReconciliationStatus.Balanced);
        snap.Drift.ShouldBe(BigInteger.Zero);
        snap.OnChainTotal.ShouldBe(new BigInteger(10_000_000));
        snap.AddressesScanned.ShouldBe(3);
        snap.AddressesUnreadable.ShouldBe(0);
        _store.HistoryCount.ShouldBe(1);
    }

    [Fact]
    public async Task More_on_chain_than_the_ledger_records_is_flagged_as_positive_drift()
    {
        _ledger.GetTreasuryHoldingAsync(Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(10_000_000));
        GivenControlledAddresses((Guid.CreateVersion7(), Deposit1));

        var reader = new InMemoryBalanceReader();
        reader.Set(Chain.Tron, HotWallet, Usdt, 5_000_000);
        reader.Set(Chain.Tron, Deposit1, Usdt, 7_000_000); // 12M on chain vs 10M ledger

        await Build(reader).ReconcileAsync(Chain.Tron, Ct);

        var snap = _store.Latest.ShouldNotBeNull();
        snap.Status.ShouldBe(ReconciliationStatus.Drift);
        snap.Drift.ShouldBe(new BigInteger(2_000_000));
    }

    [Fact]
    public async Task A_ledger_holding_with_no_controlled_addresses_is_negative_drift_surfacing_a_missing_hot_wallet()
    {
        _ledger.GetTreasuryHoldingAsync(Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(10_000_000));
        _platform.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns([]);
        _deposits.ListReceivingDepositAddressesAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns([]);

        await Build(new InMemoryBalanceReader()).ReconcileAsync(Chain.Tron, Ct);

        var snap = _store.Latest.ShouldNotBeNull();
        snap.Status.ShouldBe(ReconciliationStatus.Drift);
        snap.OnChainTotal.ShouldBe(BigInteger.Zero);
        snap.Drift.ShouldBe(new BigInteger(-10_000_000));
    }

    [Fact]
    public async Task An_unreadable_address_makes_the_pass_incomplete_never_a_false_balanced()
    {
        _ledger.GetTreasuryHoldingAsync(Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(4_000_000));
        GivenControlledAddresses((Guid.CreateVersion7(), Deposit1));

        // Hot wallet reads fine; the deposit address throws (node/RPC failure).
        var reader = Substitute.For<IBalanceReader>();
        reader.GetBalanceAsync(Chain.Tron, HotWallet, Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(4_000_000));
        reader.GetBalanceAsync(Chain.Tron, Deposit1, Usdt, Arg.Any<CancellationToken>())
            .Returns<BigInteger>(_ => throw new InvalidOperationException("node down"));

        await Build(reader).ReconcileAsync(Chain.Tron, Ct);

        var snap = _store.Latest.ShouldNotBeNull();
        // On-chain total equals the ledger for the readable part — but because one address failed, we must NOT
        // report Balanced; the total is partial.
        snap.Status.ShouldBe(ReconciliationStatus.Incomplete);
        snap.AddressesUnreadable.ShouldBe(1);
    }

    [Fact]
    public async Task Drift_within_tolerance_is_balanced_but_the_exact_drift_is_still_recorded()
    {
        _ledger.GetTreasuryHoldingAsync(Usdt, Arg.Any<CancellationToken>()).Returns(new BigInteger(10_000_000));
        GivenControlledAddresses((Guid.CreateVersion7(), Deposit1));

        var reader = new InMemoryBalanceReader();
        reader.Set(Chain.Tron, HotWallet, Usdt, 6_000_000);
        reader.Set(Chain.Tron, Deposit1, Usdt, 4_500_000); // 10.5M vs 10M → 0.5M drift

        await Build(reader, tolerance: 1_000_000).ReconcileAsync(Chain.Tron, Ct);

        var snap = _store.Latest.ShouldNotBeNull();
        snap.Status.ShouldBe(ReconciliationStatus.Balanced);   // absorbed by tolerance
        snap.Drift.ShouldBe(new BigInteger(500_000));          // but never hidden
    }

    /// <summary>Captures the most recent snapshot + counts history appends, so tests assert the outcome.</summary>
    private sealed class CapturingStore : IReconciliationStore, IReconciliationHistoryStore
    {
        public ReconciliationSnapshot? Latest { get; private set; }
        public int HistoryCount { get; private set; }

        public Task UpsertAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Latest = snapshot;
            return Task.CompletedTask;
        }

        public Task<ReconciliationSnapshot?> GetAsync(Chain chain, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Latest);

        public Task AppendAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            HistoryCount++;
            return Task.CompletedTask;
        }
    }
}
