using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;
using CryptoPaymentEngine.SharedKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The pool allocator's selection rules: it hands out a wallet that is free (not leased by an in-flight
/// withdrawal — one tx at a time) and funded (holds ≥ the amount), chosen least-recently-used, and returns
/// null (⇒ the withdrawal parks) when none qualifies.
/// </summary>
public sealed class HotWalletAllocatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly BigInteger Amount = BigInteger.Parse("1000000");

    private readonly ITreasuryHotWalletDirectory _treasury = Substitute.For<ITreasuryHotWalletDirectory>();
    private readonly IWithdrawalRepository _repository = Substitute.For<IWithdrawalRepository>();
    private readonly IBalanceReader _balances = Substitute.For<IBalanceReader>();

    public HotWalletAllocatorTests()
    {
        Busy(); // default: nothing leased
        _repository.GetWalletLastUsedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>()));
    }

    private HotWalletAllocator NewAllocator() => new(_treasury, _repository, _balances);

    private static TreasuryHotWallet Wallet(Guid id, string address) => new(id, Chain.Tron, address, $"ref#{address}");

    private void Pool(params TreasuryHotWallet[] wallets) =>
        _treasury.GetHotWalletPoolAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TreasuryHotWallet>>([.. wallets]));

    private void Busy(params Guid[] ids) =>
        _repository.GetInFlightSourceWalletIdsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<Guid>>(ids));

    private void Balance(string address, BigInteger amount) =>
        _balances.GetBalanceAsync(Chain.Tron, address, Asset, Arg.Any<CancellationToken>()).Returns(Task.FromResult(amount));

    private void LastUsed(IReadOnlyDictionary<Guid, DateTimeOffset> map) =>
        _repository.GetWalletLastUsedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(map));

    [Fact]
    public async Task Allocates_a_free_funded_wallet()
    {
        var id = Guid.NewGuid();
        Pool(Wallet(id, "TWalletA"));
        Balance("TWalletA", Amount);

        var lease = await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct);

        lease.ShouldNotBeNull();
        lease!.WalletId.ShouldBe(id);
        lease.Address.ShouldBe("TWalletA");
    }

    [Fact]
    public async Task Returns_null_when_the_pool_is_empty()
    {
        Pool();
        (await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Skips_leased_wallets_and_picks_a_free_one()
    {
        var busy = Guid.NewGuid();
        var free = Guid.NewGuid();
        Pool(Wallet(busy, "TBusy"), Wallet(free, "TFree"));
        Busy(busy);
        Balance("TBusy", Amount);
        Balance("TFree", Amount);

        var lease = await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct);
        lease!.WalletId.ShouldBe(free);
    }

    [Fact]
    public async Task Returns_null_when_every_wallet_is_leased()
    {
        var id = Guid.NewGuid();
        Pool(Wallet(id, "TWallet"));
        Busy(id);
        Balance("TWallet", Amount);

        (await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Skips_underfunded_wallets()
    {
        var poor = Guid.NewGuid();
        var rich = Guid.NewGuid();
        Pool(Wallet(poor, "TPoor"), Wallet(rich, "TRich"));
        Balance("TPoor", Amount - BigInteger.One);
        Balance("TRich", Amount);

        var lease = await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct);
        lease!.WalletId.ShouldBe(rich);
    }

    [Fact]
    public async Task Returns_null_when_no_wallet_holds_enough()
    {
        var id = Guid.NewGuid();
        Pool(Wallet(id, "TWallet"));
        Balance("TWallet", Amount - BigInteger.One);

        (await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Picks_the_least_recently_used_among_free_funded_wallets()
    {
        var recent = Guid.NewGuid();
        var stale = Guid.NewGuid();
        Pool(Wallet(recent, "TRecent"), Wallet(stale, "TStale"));
        Balance("TRecent", Amount);
        Balance("TStale", Amount);
        LastUsed(new Dictionary<Guid, DateTimeOffset>
        {
            [recent] = DateTimeOffset.UtcNow,
            [stale] = DateTimeOffset.UtcNow.AddHours(-1),
        });

        var lease = await NewAllocator().AllocateAsync(Chain.Tron, Asset, Amount, Ct);
        lease!.WalletId.ShouldBe(stale); // older last-use ⇒ picked first
    }
}
