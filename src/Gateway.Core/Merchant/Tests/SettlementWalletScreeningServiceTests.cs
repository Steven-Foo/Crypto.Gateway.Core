using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// Periodic re-screening of whitelisted settlement wallets.
///
/// <para>The behaviour worth protecting is what it does NOT do: a worsened verdict never removes a wallet.
/// Auto-revoking would let a vendor's opinion, or its outage, halt a merchant's earnings with no human in
/// the loop — and a human is already in the loop, because every cash-out stops for audit.</para>
/// </summary>
public sealed class SettlementWalletScreeningServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string WalletA = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string WalletB = "TQp6K2nHpqdk5d6q8VjAYLvp1ufUKVpsts";

    private static (SettlementWalletScreeningService Service, StubScreening Screening) Build(
        bool enabled = true, params SettlementWalletRef[] wallets)
    {
        var screening = new StubScreening();

        var service = new SettlementWalletScreeningService(
            new StubSettlements(wallets),
            screening,
            Options.Create(new MerchantScreeningOptions { RescreenSettlementWallets = enabled }),
            NullLogger<SettlementWalletScreeningService>.Instance);

        return (service, screening);
    }

    private static SettlementWalletRef Wallet(string address) =>
        new(Guid.CreateVersion7(), Chain.Tron, address);

    /// <summary>
    /// The opt-in guard. Proven by a stub that throws if reached, so this test passing is evidence that
    /// nothing was called rather than an absence of evidence that something was.
    /// </summary>
    [Fact]
    public async Task With_rescreening_off_no_wallet_is_ever_screened()
    {
        var service = new SettlementWalletScreeningService(
            new StubSettlements([Wallet(WalletA)]),
            new NeverCalledScreening(),
            Options.Create(new MerchantScreeningOptions { RescreenSettlementWallets = false }),
            NullLogger<SettlementWalletScreeningService>.Instance);

        var result = await service.RescreenOnceAsync(Ct);

        result.Total.ShouldBe(0);
        result.Screened.ShouldBe(0);
    }

    /// <summary>
    /// A host that composes Merchant without Compliance must not crash, and must not silently report that
    /// everything was checked. It does nothing and says so loudly.
    /// </summary>
    [Fact]
    public async Task With_no_provider_composed_it_does_nothing_rather_than_throwing()
    {
        var service = new SettlementWalletScreeningService(
            new StubSettlements([Wallet(WalletA)]),
            screening: null,
            Options.Create(new MerchantScreeningOptions { RescreenSettlementWallets = true }),
            NullLogger<SettlementWalletScreeningService>.Instance);

        var result = await service.RescreenOnceAsync(Ct);

        result.Screened.ShouldBe(0);
    }

    /// <summary>
    /// A still-fresh verdict costs nothing. This is why the pass interval does not drive quota — the cache
    /// window does — and why running the worker often is cheap.
    /// </summary>
    [Fact]
    public async Task A_wallet_whose_verdict_is_still_fresh_is_not_counted_as_screened()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));
        screening.ServeFromCache = true;

        var result = await service.RescreenOnceAsync(Ct);

        result.Total.ShouldBe(1);
        result.Screened.ShouldBe(0, "a cached verdict means no provider call was made");
        result.Flagged.ShouldBe(0);
    }

    [Fact]
    public async Task Every_stale_wallet_is_re_screened()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA), Wallet(WalletB));

        var result = await service.RescreenOnceAsync(Ct);

        result.Total.ShouldBe(2);
        result.Screened.ShouldBe(2);
        screening.ScreenCalls.ShouldBe(2);
    }

    /// <summary>The purpose is recorded so a later audit can tell a settlement-wallet check apart from a
    /// payout-destination one, even though both screen the same kind of address.</summary>
    [Fact]
    public async Task It_screens_under_the_settlement_wallet_purpose()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));

        await service.RescreenOnceAsync(Ct);

        screening.LastPurpose.ShouldBe(ScreeningPurpose.SettlementWallet);
    }

    [Fact]
    public async Task A_verdict_that_worsens_is_flagged()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));
        screening.Previous = ScreeningDecision.Allow;
        screening.Current = ScreeningDecision.Block;

        var result = await service.RescreenOnceAsync(Ct);

        result.Flagged.ShouldBe(1);
    }

    /// <summary>
    /// The whole point of the restraint. A Block on a wallet already on file records evidence and warns —
    /// it does not touch the whitelist, so the merchant's cash-out destination is unchanged and staff
    /// decide at the audit step that every cash-out already passes through.
    /// </summary>
    [Fact]
    public async Task A_blocked_verdict_flags_the_wallet_but_never_revokes_it()
    {
        var settlements = new StubSettlements([Wallet(WalletA)]);
        var screening = new StubScreening { Previous = ScreeningDecision.Allow, Current = ScreeningDecision.Block };

        var service = new SettlementWalletScreeningService(
            settlements, screening,
            Options.Create(new MerchantScreeningOptions { RescreenSettlementWallets = true }),
            NullLogger<SettlementWalletScreeningService>.Instance);

        var result = await service.RescreenOnceAsync(Ct);

        result.Flagged.ShouldBe(1);

        // The directory is read-only from here. Nothing in this service can remove a wallet, and the stub
        // would have to expose a mutation for it to try.
        settlements.Wallets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_verdict_that_stays_the_same_is_not_flagged_again()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));
        screening.Previous = ScreeningDecision.Block;
        screening.Current = ScreeningDecision.Block;

        var result = await service.RescreenOnceAsync(Ct);

        result.Screened.ShouldBe(1);
        result.Flagged.ShouldBe(0, "an unchanged bad verdict is not news");
    }

    [Fact]
    public async Task A_verdict_that_improves_is_not_flagged()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));
        screening.Previous = ScreeningDecision.Block;
        screening.Current = ScreeningDecision.Allow;

        var result = await service.RescreenOnceAsync(Ct);

        result.Flagged.ShouldBe(0);
    }

    /// <summary>
    /// A provider outage is not news about the address. Treating it as a downgrade would raise an alarm
    /// every time the vendor had a bad afternoon, which is how a real alert gets ignored.
    /// </summary>
    [Fact]
    public async Task An_unavailable_verdict_is_never_treated_as_a_downgrade()
    {
        var (service, screening) = Build(enabled: true, Wallet(WalletA));
        screening.Previous = ScreeningDecision.Allow;
        screening.Current = ScreeningDecision.Unavailable;

        var result = await service.RescreenOnceAsync(Ct);

        result.Screened.ShouldBe(1);
        result.Flagged.ShouldBe(0);
    }

    /// <summary>A wallet whitelisted before screening existed has no prior verdict. A first result that is
    /// bad is worth flagging; a first result that is clean is the ordinary case.</summary>
    [Fact]
    public async Task A_first_ever_verdict_is_flagged_only_when_it_is_bad()
    {
        var (clean, cleanScreening) = Build(enabled: true, Wallet(WalletA));
        cleanScreening.Previous = null;
        cleanScreening.Current = ScreeningDecision.Allow;
        (await clean.RescreenOnceAsync(Ct)).Flagged.ShouldBe(0);

        var (bad, badScreening) = Build(enabled: true, Wallet(WalletB));
        badScreening.Previous = null;
        badScreening.Current = ScreeningDecision.Review;
        (await bad.RescreenOnceAsync(Ct)).Flagged.ShouldBe(1);
    }

    private sealed class StubSettlements(IReadOnlyList<SettlementWalletRef> wallets) : IMerchantSettlementDirectory
    {
        public IReadOnlyList<SettlementWalletRef> Wallets { get; } = wallets;

        public Task<string?> FindSettlementAddressAsync(
            Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult(Wallets.FirstOrDefault(w => w.MerchantId == merchantId && w.Chain == chain)?.Address);

        public Task<IReadOnlyList<SettlementWalletRef>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Wallets);
    }

    private sealed class StubScreening : IAddressScreeningService
    {
        public ScreeningDecision? Previous { get; set; } = ScreeningDecision.Allow;
        public ScreeningDecision Current { get; set; } = ScreeningDecision.Allow;
        public bool ServeFromCache { get; set; }
        public int ScreenCalls { get; private set; }
        public ScreeningPurpose? LastPurpose { get; private set; }

        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default)
        {
            ScreenCalls++;
            LastPurpose = purpose;
            return Task.FromResult(Verdict(Current, ServeFromCache));
        }

        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(Previous is null ? null : Verdict(Previous.Value, fromCache: true));

        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The periodic pass must go through ScreenAsync so a fresh verdict is reused. "
                + "Forcing a re-screen per wallet would spend quota on every pass.");

        private static ScreeningVerdict Verdict(ScreeningDecision decision, bool fromCache) =>
            new(Guid.CreateVersion7(), decision,
                decision == ScreeningDecision.Unavailable ? null : 50,
                decision == ScreeningDecision.Unavailable ? null : "Moderate",
                ["test-indicator"], AddressLabel: null, ReportUrl: null, DateTimeOffset.UtcNow, fromCache);

        // Candidate filtering is for the address-sweep passes; these stubs stand in for money-path and
        // settlement callers, which never ask.
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NeverCalledScreening : IAddressScreeningService
    {
        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Re-screening must not run when it is switched off.");

        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Re-screening must not run when it is switched off.");

        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Re-screening must not run when it is switched off.");

        // Candidate filtering is for the address-sweep passes; these stubs stand in for money-path and
        // settlement callers, which never ask.
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
