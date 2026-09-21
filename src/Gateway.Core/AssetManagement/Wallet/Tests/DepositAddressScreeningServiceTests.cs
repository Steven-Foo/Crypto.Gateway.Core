using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Tests;

/// <summary>
/// Screening the platform's own funded deposit addresses — the inbound control, in the only form available.
///
/// <para>An inbound transfer cannot be screened while it is in flight, and once it lands it is credited and
/// never reversed. So the control watches the other side of the graph: our own receiving addresses, whose
/// score rises when funds arrive from a bad counterparty. It records and flags; it changes nothing.</para>
/// </summary>
public sealed class DepositAddressScreeningServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DepositAddressScreeningService Build(
        StubWallets wallets, IAddressScreeningService? screening, DepositAddressScreeningOptions options) =>
        new(wallets, screening, Options.Create(options),
            NullLogger<DepositAddressScreeningService>.Instance);

    private static DepositAddressScreeningOptions Options_(bool enabled = true, int cap = 100) => new()
    {
        Enabled = enabled,
        MaxAddressesPerPass = cap,
        Chains = [Chain.Tron],
    };

    private static StubWallets WalletsWith(int count)
    {
        var addresses = Enumerable.Range(0, count)
            .Select(i => new ReceivingDepositAddress(Guid.CreateVersion7(), $"TAddr{i:D30}"))
            .ToList();

        return new StubWallets(addresses);
    }

    /// <summary>
    /// The opt-in guard, proven by a stub that throws if reached — so passing is evidence that nothing was
    /// called, rather than the absence of evidence that something was.
    /// </summary>
    [Fact]
    public async Task With_the_scheduled_pass_off_nothing_is_screened()
    {
        var service = Build(WalletsWith(3), new NeverCalledScreening(), Options_(enabled: false));

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Screened.ShouldBe(0);
    }

    /// <summary>
    /// The manual sweep deliberately bypasses the enabled switch, so staff can check on demand without
    /// committing to a standing spend. That is the reason the two exist separately.
    /// </summary>
    [Fact]
    public async Task The_manual_sweep_runs_even_when_the_scheduled_pass_is_off()
    {
        var screening = new StubScreening();
        var service = Build(WalletsWith(3), screening, Options_(enabled: false));

        var result = await service.ScreenOnceAsync(force: true, Ct);

        result.Screened.ShouldBe(3);
    }

    /// <summary>
    /// The most important guard here. Deposit addresses are the one candidate set that grows without bound,
    /// and this shares a quota with the payout gate — the control that actually holds money. An uncapped
    /// sweep could spend a day's budget and leave payouts unable to be screened.
    /// </summary>
    [Fact]
    public async Task A_pass_never_spends_more_calls_than_its_cap()
    {
        var screening = new StubScreening();
        var service = Build(WalletsWith(500), screening, Options_(cap: 10));

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Screened.ShouldBe(10);
        screening.ScreenCalls.ShouldBe(10);
    }

    /// <summary>A manual run is not a licence to spend the day's quota. It costs exactly what a scheduled
    /// pass costs.</summary>
    [Fact]
    public async Task The_manual_sweep_honours_the_same_cap()
    {
        var screening = new StubScreening();
        var service = Build(WalletsWith(500), screening, Options_(enabled: false, cap: 5));

        var result = await service.ScreenOnceAsync(force: true, Ct);

        result.Screened.ShouldBe(5);
    }

    /// <summary>
    /// Candidates and screened diverging is how an operator learns the cap is biting and a backlog is
    /// building, rather than finding out because addresses quietly go unscreened for longer and longer.
    /// </summary>
    [Fact]
    public async Task A_capped_pass_reports_how_many_were_due()
    {
        var service = Build(WalletsWith(50), new StubScreening(), Options_(cap: 10));

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Candidates.ShouldBe(10, "the candidate query is asked for at most the budget");
        result.Screened.ShouldBe(10);
    }

    /// <summary>Addresses already holding a fresh verdict cost nothing, which is why the cache window and
    /// not the pass interval drives quota consumption.</summary>
    [Fact]
    public async Task Addresses_with_a_fresh_verdict_are_not_screened_again()
    {
        var wallets = WalletsWith(5);
        var screening = new StubScreening { NothingIsDue = true };
        var service = Build(wallets, screening, Options_());

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Candidates.ShouldBe(0);
        result.Screened.ShouldBe(0);
        screening.ScreenCalls.ShouldBe(0);
    }

    /// <summary>The purpose separates "our own address" from a counterparty, so a later audit can tell the
    /// two apart even though both are just addresses.</summary>
    [Fact]
    public async Task It_screens_under_the_deposit_address_purpose()
    {
        var screening = new StubScreening();
        var service = Build(WalletsWith(1), screening, Options_());

        await service.ScreenOnceAsync(force: false, Ct);

        screening.LastPurpose.ShouldBe(ScreeningPurpose.DepositAddress);
    }

    [Fact]
    public async Task A_risky_address_is_flagged()
    {
        var screening = new StubScreening { Decision = ScreeningDecision.Block };
        var service = Build(WalletsWith(2), screening, Options_());

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Flagged.ShouldBe(2);
    }

    /// <summary>An outage is not news about the address, and alarming on it is how a real alert gets
    /// ignored.</summary>
    [Fact]
    public async Task An_unavailable_verdict_is_not_a_flag()
    {
        var screening = new StubScreening { Decision = ScreeningDecision.Unavailable };
        var service = Build(WalletsWith(2), screening, Options_());

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Screened.ShouldBe(2);
        result.Flagged.ShouldBe(0);
    }

    [Fact]
    public async Task With_no_provider_composed_it_does_nothing_rather_than_throwing()
    {
        var service = Build(WalletsWith(3), screening: null, Options_());

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Screened.ShouldBe(0);
    }

    [Fact]
    public async Task No_funded_addresses_is_a_quiet_no_op()
    {
        var screening = new StubScreening();
        var service = Build(WalletsWith(0), screening, Options_());

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Candidates.ShouldBe(0);
        screening.ScreenCalls.ShouldBe(0);
    }

    /// <summary>
    /// Found live, and it cost real quota. .NET binds a configuration array by ADDING to whatever the
    /// property already holds, so a settings file listing ["Tron"] against a default of [Tron] produces
    /// [Tron, Tron] — and the sweep looped twice over the same chain, screening every address twice.
    ///
    /// <para>The same binding behaviour was found earlier on Compliance:AlwaysBlockIndicators, where it was
    /// only noisy. Here it doubles the bill. Any array-typed option in this codebase behaves this way.</para>
    /// </summary>
    [Fact]
    public async Task A_chain_listed_twice_by_configuration_binding_is_only_swept_once()
    {
        var screening = new StubScreening();
        var options = Options_();

        // Exactly what the binder produces from a config file that names the default chain.
        options.Chains = [Chain.Tron, Chain.Tron];

        var service = Build(WalletsWith(3), screening, options);

        var result = await service.ScreenOnceAsync(force: false, Ct);

        result.Screened.ShouldBe(3, "three addresses, screened once each");
        screening.ScreenCalls.ShouldBe(3);
    }

    private sealed class StubWallets(IReadOnlyList<ReceivingDepositAddress> addresses) : IWalletDirectory
    {
        public Task<IReadOnlyList<ReceivingDepositAddress>> ListReceivingDepositAddressesAsync(
            Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult(addresses);

        public Task<WalletOwnership?> FindByAddressAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<AvailableWallet> Items, int TotalCount)> SearchAssignedWalletsAsync(
            Guid merchantId, Chain chain, int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AvailableWallet>> ListAssignedWalletsAsync(
            Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubScreening : IAddressScreeningService
    {
        public ScreeningDecision Decision { get; set; } = ScreeningDecision.Allow;
        public bool NothingIsDue { get; set; }
        public int ScreenCalls { get; private set; }
        public ScreeningPurpose? LastPurpose { get; private set; }

        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default)
        {
            ScreenCalls++;
            LastPurpose = purpose;
            return Task.FromResult(new ScreeningVerdict(
                Guid.CreateVersion7(), Decision,
                Decision == ScreeningDecision.Unavailable ? null : 80,
                Decision == ScreeningDecision.Unavailable ? null : "High",
                ["test-indicator"], AddressLabel: null, ReportUrl: null,
                DateTimeOffset.UtcNow, FromCache: false));
        }

        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreeningVerdict?>(null);

        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The sweep must go through ScreenAsync so a fresh verdict is reused. Forcing a re-screen per "
                + "address would spend quota on every pass.");

        // Mirrors the real service: returns at most `limit`, which is what bounds a pass before it starts.
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(NothingIsDue ? [] : [.. addresses.Take(limit)]);
    }

    private sealed class NeverCalledScreening : IAddressScreeningService
    {
        public Task<ScreeningVerdict> ScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not run when the scheduled pass is off.");

        public Task<ScreeningVerdict?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not run when the scheduled pass is off.");

        public Task<ScreeningVerdict> ReScreenAsync(
            Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not run when the scheduled pass is off.");

        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screening must not run when the scheduled pass is off.");
    }
}
