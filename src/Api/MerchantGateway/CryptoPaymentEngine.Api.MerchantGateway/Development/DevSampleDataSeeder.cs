using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.MerchantGateway.Development;

/// <summary>
/// DEV/TESTNET ONLY. Seeds a realistic demo portfolio — several merchants with different pricing and
/// settlement terms, deposit invoices in varied states, and withdrawals sitting in every status a UI has to
/// render — so back-office and merchant-portal development has something to display without anyone sending
/// real crypto.
///
/// <para><b>The design rule that matters:</b> this seeder never writes money rows. It creates only
/// <em>inputs</em> — merchants, invoices, scripted blocks on the in-memory chain, and withdrawal requests —
/// and lets the real detection/confirmation/ledger/callback pipeline produce every deposit, journal, balance
/// and callback exactly as it would in production. Hand-writing double-entry rows would bypass every
/// invariant the ledger exists to enforce (§14, §15) and would drift from the code the moment either changed.
/// The only thing standing in for reality is the node itself, at the §8 DI seam the in-memory chain source
/// already occupies.</para>
///
/// <para>Consequences of that choice: it needs the in-memory chain (<c>Chains:Tron:Live=false</c>) — under a
/// live node the only way to make a deposit appear is to actually send USDT — and it is not instant, because
/// it waits for the same workers a real deposit waits for. Idempotent: a re-run detects the demo merchants
/// and stops, so it can never double-seed.</para>
/// </summary>
public sealed class DevSampleDataSeeder(
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    IOptions<DevSampleDataOptions> options,
    ILogger<DevSampleDataSeeder> logger) : IHostedService
{
    private const decimal Usdt = 1_000_000m;   // USDT-TRON: 6 dp
    private const string MarkerMerchantCode = "DEMOACME";

    private readonly DevSampleDataOptions _options = options.Value;
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return Task.CompletedTask;

        // Runs detached: the deposit half has to wait on the very background workers that only start once
        // this method returns, so blocking here would deadlock the thing it is waiting for.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (services.GetService<InMemoryChainSource>() is not { } chain)
            {
                logger.LogInformation(
                    "Dev sample data skipped: a live chain adapter is in use. Set Chains:Tron:Live=false (and "
                    + "Withdrawal:LiveTron=false) to seed demo data, or send real funds instead.");
                return;
            }

            await using (var probe = scopeFactory.CreateAsyncScope())
            {
                var merchants = probe.ServiceProvider.GetRequiredService<IMerchantDirectory>();
                if (await merchants.FindByCodeAsync(MarkerMerchantCode, ct) is not null)
                {
                    logger.LogInformation(
                        "Dev sample data already present ({Code} exists) — skipping. Drop the database to re-seed.",
                        MarkerMerchantCode);
                    return;
                }
            }

            var asset = await ResolveUsdtAsync(ct);
            if (asset is null)
            {
                logger.LogWarning("Dev sample data skipped: no USDT-TRON asset is configured.");
                return;
            }

            logger.LogInformation("Seeding dev sample data — this takes a minute or two while the real workers converge.");

            var profiles = await SeedMerchantsAsync(asset.AssetId, ct);
            if (profiles.Count == 0)
            {
                logger.LogWarning("Dev sample data: no demo merchants could be created — stopping.");
                return;
            }

            await SeedDepositsAsync(chain, profiles, asset, ct);
            await SeedResourceObservationsAsync(ct);

            // Money-out needs money in. The deposits above are credited by the same workers a real deposit
            // waits for, so wait for the ledger rather than assume it.
            var funded = await WaitForCreditedBalancesAsync(profiles, asset.AssetId, ct);
            if (!funded)
            {
                logger.LogWarning(
                    "Dev sample data: deposits had not been credited after {Seconds}s, so withdrawals were not "
                    + "seeded. The deposits still land — re-run the host to add the withdrawals.",
                    _options.LedgerWaitSeconds);
                return;
            }

            await SeedWithdrawalsAsync(profiles, asset.AssetId, ct);

            // Settlement periods + freezes go on last, so every demo merchant has history AND its term.
            await ApplyFinalTermsAsync(profiles, ct);

            logger.LogInformation(
                "Dev sample data seeded: {Count} demo merchants with invoices, credited deposits and withdrawals "
                + "across every status. See docs/dev-sample-data.md.",
                profiles.Count);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-seed. Nothing to report — a re-run resumes.
        }
        catch (Exception ex)
        {
            // A dev convenience must never brick a running host. Usual cause: an un-migrated schema.
            logger.LogWarning(ex, "Dev sample data seeding failed. Is every module schema migrated on this database?");
        }
    }

    // ── Merchants ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three merchants that differ in the ways the UI must actually handle: pricing, settlement period, cash-out
    /// cap and lifecycle status. A frozen merchant is deliberately included — a UI that only ever sees healthy
    /// tenants tends to render the unhealthy ones wrong.
    /// </summary>
    private async Task<List<DemoMerchant>> SeedMerchantsAsync(Guid assetId, CancellationToken ct)
    {
        var blueprints = new[]
        {
            new Blueprint(MarkerMerchantCode, "Acme Payments", DepositBps: 100, WithdrawalBps: 50,
                SettlementDelayDays: 0, CashOutPercentBps: 0, Freeze: false,
                SettlementAddress: "TDemoAcmeSettlementWalletAddr0001"),
            new Blueprint("DEMOGLOBE", "Globe Commerce", DepositBps: 150, WithdrawalBps: 75,
                SettlementDelayDays: 1, CashOutPercentBps: 5_000, Freeze: false,
                SettlementAddress: "TDemoGlobeSettlementWalletAddr001"),
            new Blueprint("DEMOFROST", "Frostbite Retail (frozen)", DepositBps: 100, WithdrawalBps: 50,
                SettlementDelayDays: 2, CashOutPercentBps: 0, Freeze: true,
                SettlementAddress: "TDemoFrostSettlementWalletAddr001"),
        };

        var created = new List<DemoMerchant>();

        foreach (var blueprint in blueprints)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var registrar = scope.ServiceProvider.GetRequiredService<IMerchantRegistrar>();
            var policies = scope.ServiceProvider.GetRequiredService<IMerchantAssetPolicyService>();

            var registration = await registrar.RegisterAsync(
                blueprint.Code, blueprint.Name, _options.CallbackUrl, ct);
            if (registration.IsFailure)
            {
                logger.LogWarning("Dev sample data: could not register {Code}: {Error}.",
                    blueprint.Code, registration.Error!.Message);
                continue;
            }

            var merchantId = registration.Value.MerchantId;

            // Pricing through the same services the staff Ops endpoints call — validated, not fabricated.
            await policies.SetFeesAsync(merchantId, assetId,
                BigInteger.Zero, blueprint.DepositBps, BigInteger.Zero, blueprint.WithdrawalBps, cancellationToken: ct);
            await registrar.SetSettlementWalletAsync(merchantId, Chain.Tron, blueprint.SettlementAddress, ct);

            if (blueprint.CashOutPercentBps > 0)
                await policies.SetMerchantWithdrawalCapAsync(merchantId, assetId, flatCap: null, blueprint.CashOutPercentBps, ct);

            // NOTE: the settlement period and the freeze are applied LAST (see ApplyFinalTermsAsync), not here.
            created.Add(new DemoMerchant(merchantId, blueprint.Code, blueprint.Freeze, blueprint.SettlementDelayDays));

            // The API key is a public identifier; the secrets are NOT logged (§10). A developer who needs to
            // sign as a demo merchant rotates its credential from the back office.
            logger.LogInformation("Dev sample data: registered merchant {Code} ({Id}) with X-Api-Key {ApiKey}.",
                blueprint.Code, merchantId, registration.Value.ApiKey);
        }

        return created;
    }

    // ── Deposits ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates invoices, then scripts the in-memory chain so the real scanner detects them and the real
    /// confirmation worker credits them. Some invoices are deliberately left unpaid so the UI has genuine
    /// pending rows, and one is underpaid so <c>amountMatched=false</c> is exercised.
    /// </summary>
    private async Task SeedDepositsAsync(
        InMemoryChainSource chain, List<DemoMerchant> merchants, AssetDto asset, CancellationToken ct)
    {
        // (invoiced net amount, actually paid — null means the payer never turned up)
        var script = new (decimal Invoiced, decimal? Paid)[]
        {
            (1_500m, 1_500m),
            (750m, 750m),
            (2_000m, 2_000m),
            (325.50m, 325.50m),
            (99m, null),      // waiting on the payer
            (480m, 470m),     // underpaid: matches the invoice, flips amountMatched to false
        };

        var toPay = new List<(string Address, BigInteger Amount)>();

        foreach (var merchant in merchants)
        {
            for (var i = 0; i < script.Length; i++)
            {
                var (invoiced, paid) = script[i];
                var reference = $"{merchant.Code}-INV-{i + 1:D3}";

                await using var scope = scopeFactory.CreateAsyncScope();
                var intents = scope.ServiceProvider.GetRequiredService<IPaymentIntentService>();

                var result = await intents.CreateAsync(new CreatePaymentIntentCommand(
                    merchant.MerchantId,
                    reference,
                    Chain.Tron,
                    asset.AssetId,
                    ToBaseUnits(invoiced),
                    _options.CallbackUrl), ct);

                if (result.IsFailure)
                {
                    logger.LogInformation("Dev sample data: invoice {Ref} not created ({Error}).",
                        reference, result.Error!.Code);
                    continue;
                }

                if (paid is not null)
                    toPay.Add((result.Value.Address, ToBaseUnits(paid.Value)));
            }
        }

        if (toPay.Count > 0)
            await ScriptChainAsync(chain, toPay, asset.AssetId, ct);
    }

    /// <summary>
    /// Writes one block carrying every seeded transfer, buries it to the policy confirmation depth, and points
    /// the scan cursor just behind it. Without the cursor the scanner cold start jumps straight to the tip and
    /// skips the block we just wrote.
    /// </summary>
    private async Task ScriptChainAsync(
        InMemoryChainSource chain, List<(string Address, BigInteger Amount)> transfers, Guid assetId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var cursors = scope.ServiceProvider.GetRequiredService<IScanCursorStore>();
        var policies = scope.ServiceProvider.GetRequiredService<IDepositPolicyProvider>();

        var required = policies.For(Chain.Tron).RequiredConfirmations;
        var tip = await chain.GetTipHeightAsync(Chain.Tron, ct);
        var block = (tip > 0 ? tip : 1_000) + 1;

        var detected = transfers
            .Select((t, index) => new DetectedTransfer(
                Chain.Tron, t.Address, assetId, t.Amount,
                $"0xdemoseed{block:D8}{index:D2}", 0, block, $"h{block}"))
            .ToArray();

        chain.AddBlock(Chain.Tron, block, $"h{block}", detected);
        for (var i = 1; i <= required; i++)
            chain.AddBlock(Chain.Tron, block + i, $"h{block + i}");

        await cursors.SetLastScannedBlockAsync(Chain.Tron, block - 1, ct);

        logger.LogInformation(
            "Dev sample data: scripted {Count} transfers into block {Block}, buried {Depth} deep. The scanner "
            + "picks them up within ~20s.", detected.Length, block, required);
    }

    // ── Withdrawals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One withdrawal per status a back-office operator has to act on, requested through the same services the
    /// merchant API and portal call — so the approval routing, fee, settled-balance gate and ledger reserve are
    /// all genuinely applied rather than implied by a status string.
    /// </summary>
    private async Task SeedWithdrawalsAsync(List<DemoMerchant> merchants, Guid assetId, CancellationToken ct)
    {
        // Every merchant, including the one that will be frozen: the freeze is applied after this step, so the
        // frozen tenant ends up with history AND the freeze — the realistic shape, and the one that actually
        // exercises a UI. Its FUTURE money-out is still genuinely refused.
        foreach (var merchant in merchants)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var payouts = scope.ServiceProvider.GetRequiredService<IWithdrawalRequestService>();
            var cashOuts = scope.ServiceProvider.GetRequiredService<IMerchantWithdrawalService>();

            // Below the approval threshold (1,000 USDT): clears automatically and settles.
            await RequestPayoutAsync(payouts, merchant, assetId, 50m, "PAYOUT-SMALL", requiresMerchantApproval: false, ct);

            // Above the threshold: parks in PendingApproval for platform staff — the Ops approval queue.
            await RequestPayoutAsync(payouts, merchant, assetId, 2_500m, "PAYOUT-LARGE", requiresMerchantApproval: false, ct);

            // Portal-initiated: waits for the MERCHANT own approver first — the portal approval queue.
            await RequestPayoutAsync(payouts, merchant, assetId, 120m, "PAYOUT-PORTAL", requiresMerchantApproval: true, ct);

            // The merchant cashing out its own earnings — a different kind, same pipeline.
            var cashOut = await cashOuts.RequestAsync(new MerchantWithdrawalCommand(
                merchant.MerchantId, assetId, Chain.Tron, ToBaseUnits(200m), $"{merchant.Code}-CASHOUT-001"), ct);
            LogWithdrawal(merchant, "CASHOUT", cashOut);
        }
    }

    /// <summary>
    /// Applies the settlement period and the freeze <b>last</b>, once each merchant already has history.
    ///
    /// <para>Order matters, and not for cosmetic reasons. Both terms are gates on <em>new</em> activity: a T+N
    /// merchant has no settled balance on the day its deposits land, and a frozen merchant is refused every
    /// money-out. Applying them before the withdrawals would have been perfectly correct and left two of the
    /// three demo merchants with an empty payout list — which is a poor demo, because a UI that has only ever
    /// rendered one healthy tenant has not really been tested. Applying them after gives each merchant a full
    /// history AND the term, which is also the more realistic shape: terms change on established merchants.</para>
    ///
    /// <para>Nothing is faked by doing this — every gate is still live. A payout submitted against the T+1
    /// merchant from the portal right now is still correctly refused, and the frozen merchant still refuses
    /// everything. The difference is only that their past is populated.</para>
    /// </summary>
    private async Task ApplyFinalTermsAsync(List<DemoMerchant> merchants, CancellationToken ct)
    {
        foreach (var merchant in merchants)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var registrar = scope.ServiceProvider.GetRequiredService<IMerchantRegistrar>();

            if (merchant.SettlementDelayDays > 0)
            {
                await registrar.SetSettlementDelayAsync(merchant.MerchantId, merchant.SettlementDelayDays, ct);
                logger.LogInformation("Dev sample data: {Code} is now on T+{Days} — its available and settled balances now differ.",
                    merchant.Code, merchant.SettlementDelayDays);
            }

            if (merchant.Freeze)
            {
                await registrar.FreezeAsync(merchant.MerchantId, ct);
                logger.LogInformation("Dev sample data: froze {Code} so the UI has a non-Active tenant with history.", merchant.Code);
            }
        }
    }

    private async Task RequestPayoutAsync(
        IWithdrawalRequestService payouts, DemoMerchant merchant, Guid assetId, decimal amount,
        string reference, bool requiresMerchantApproval, CancellationToken ct)
    {
        var result = await payouts.RequestAsync(new RequestWithdrawalCommand(
            merchant.MerchantId, assetId, Chain.Tron,
            DestinationAddress: "TDemoPayoutDestinationAddress0001",
            ToBaseUnits(amount),
            $"{merchant.Code}-{reference}",
            _options.CallbackUrl,
            requiresMerchantApproval), ct);

        LogWithdrawal(merchant, reference, result);
    }

    private void LogWithdrawal(DemoMerchant merchant, string reference, Result<WithdrawalResult> result)
    {
        // A business rejection here is data, not an error: a merchant on T+2 has no settled balance yet, which
        // is exactly the behaviour the settlement period exists to produce.
        if (result.IsFailure)
            logger.LogInformation("Dev sample data: {Code} {Reference} was refused ({Error}) — expected for some profiles.",
                merchant.Code, reference, result.Error!.Code);
        else
            logger.LogInformation("Dev sample data: {Code} {Reference} created in status {Status}.",
                merchant.Code, reference, result.Value.Status);
    }

    // ── Resource observations (derived read model, never money truth §2) ─────────

    /// <summary>
    /// Gives the in-memory resource reader varied answers so the Energy monitor writes a realistic spread of
    /// resource-health snapshots to Mongo, instead of every wallet reading identically healthy. Still the real
    /// worker doing the real observation — only what it observes is staged.
    ///
    /// <para>The readings must be staged against the addresses the monitor actually polls, which are the
    /// <b>platform</b> wallets it enumerates through <c>IPlatformWalletDirectory</c> — not merchant addresses.
    /// Staging a low reading on an address nobody monitors produces no snapshot at all, which looks
    /// indistinguishable from "everything is healthy".</para>
    /// </summary>
    private async Task SeedResourceObservationsAsync(CancellationToken ct)
    {
        // Resolved through the PORT and type-checked, not by concrete type: the in-memory reader is registered
        // only as IAccountResourceReader, so GetService<InMemoryAccountResourceReader>() silently returns null
        // and this whole step would no-op with no error. (The in-memory BALANCE reader is registered
        // concretely, which is why the float seeder can ask for it directly — the two differ.)
        if (services.GetService<IAccountResourceReader>() is not InMemoryAccountResourceReader resources)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var wallets = await scope.ServiceProvider
            .GetRequiredService<IPlatformWalletDirectory>()
            .GetPlatformWalletsAsync(Chain.Tron, ct);

        if (wallets.Count == 0)
        {
            logger.LogInformation(
                "Dev sample data: no platform wallets to stage energy readings against — the resource-health "
                + "screen will show whatever the monitor observes on its own.");
            return;
        }

        // Varied energy readings, so the resource screen shows a spread of numbers rather than N identical
        // rows. NOTE: the reported HEALTH will still be "Healthy" for these wallets — the monitor classifies
        // against an EnergyPolicy, and no policy exists for HotWithdrawal wallets today (a documented
        // follow-up: 5a only alerts where a policy is configured). Seeding a policy here would be inventing
        // an operational threshold the platform has not actually chosen, so this stops at the readings.
        var staged = 0;
        foreach (var (wallet, energy) in wallets.Zip(new BigInteger[] { 50_000, 400_000 }))
        {
            resources.SetEnergyAvailable(Chain.Tron, wallet.Address, energy);
            staged++;
        }

        logger.LogInformation(
            "Dev sample data: staged {Count} reduced energy readings on monitored platform wallets (health stays "
            + "Healthy until an EnergyPolicy exists for HotWithdrawal).", staged);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<AssetDto?> ResolveUsdtAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAssetCatalog>().FindAsync(Chain.Tron, "USDT", ct);
    }

    /// <summary>Polls the ledger until every demo merchant has a credited balance, so the withdrawals are
    /// requested against real money rather than hope.</summary>
    private async Task<bool> WaitForCreditedBalancesAsync(
        List<DemoMerchant> merchants, Guid assetId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.LedgerWaitSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            await using var scope = scopeFactory.CreateAsyncScope();
            var ledger = scope.ServiceProvider.GetRequiredService<ILedgerQuery>();

            var allFunded = true;
            foreach (var merchant in merchants)
            {
                if (await ledger.GetMerchantBalanceAsync(merchant.MerchantId, assetId, ct) <= BigInteger.Zero)
                {
                    allFunded = false;
                    break;
                }
            }

            if (allFunded)
            {
                logger.LogInformation("Dev sample data: deposits credited — seeding withdrawals.");
                return true;
            }
        }

        return false;
    }

    /// <summary>Display to base units. Demo amounts only; the money path itself never does decimal maths (§14).</summary>
    private static BigInteger ToBaseUnits(decimal display) => new(decimal.Truncate(display * Usdt));

    private sealed record Blueprint(
        string Code, string Name, int DepositBps, int WithdrawalBps, int SettlementDelayDays,
        int CashOutPercentBps, bool Freeze, string SettlementAddress);

    private sealed record DemoMerchant(Guid MerchantId, string Code, bool Freeze, int SettlementDelayDays);
}

public static class DevSampleDataSeederExtensions
{
    /// <summary>Registers the demo-data seeder. Register it LAST among the dev seeders — it builds on the
    /// merchant, wallet, treasury and key-custody seeds that must already have run.</summary>
    public static IServiceCollection AddDevelopmentSampleData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevSampleDataOptions>(configuration.GetSection(DevSampleDataOptions.SectionName));
        services.AddHostedService<DevSampleDataSeeder>();
        return services;
    }
}
