using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff treasury actions: register the watch-only cold treasury address (the destination Sweep concentrates
/// deposits into), list the hot withdrawal pool with live balances, and record hot-pool top-ups. <b>Keyless</b>:
/// nothing here builds, signs or broadcasts a transaction (§10). Funds leave the cold treasury outside this system,
/// signed with the cold key in the operator's own wallet; the in-system cold reload was removed on 2026-09-17.
/// </summary>
public static class OpsTreasuryEndpoints
{
    public static void MapOpsTreasuryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/treasury/hot-pool", HotPoolAsync).RequirePermission(OpsPermissions.Treasury.Manage);

        app.MapGet("/api/v1/ops/treasury/cold-wallets", ListColdAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/cold-wallets", RegisterColdAsync).RequirePermission(OpsPermissions.Treasury.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryColdWallet);

        // The original singular path, kept so screens already built against it keep working. Same handler.
        app.MapPost("/api/v1/ops/treasury/cold-wallet", RegisterColdAsync).RequirePermission(OpsPermissions.Treasury.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryColdWallet);

        // Activate and retire decide WHERE every future sweep lands, so they are guarded like registration.
        app.MapPost("/api/v1/ops/treasury/cold-wallets/{walletId:guid}/activate", ActivateColdAsync)
            .RequirePermission(OpsPermissions.Treasury.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryColdWallet);
        app.MapPost("/api/v1/ops/treasury/cold-wallets/{walletId:guid}/retire", RetireColdAsync)
            .RequirePermission(OpsPermissions.Treasury.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryColdWallet);

        // Re-screening is deliberately NOT guarded: it changes no destination and moves nothing, it only
        // refreshes a verdict. Making an operator produce a code to look something up is how a control
        // becomes the thing people work around.
        app.MapPost("/api/v1/ops/treasury/cold-wallets/{walletId:guid}/re-screen", ReScreenColdAsync)
            .RequirePermission(OpsPermissions.Treasury.Manage);

        // Records company funds an admin has ALREADY moved into a hot wallet from a company wallet outside
        // platform custody, after on-chain verification. Nothing is sent from here.
        app.MapPost("/api/v1/ops/treasury/top-up", RecordTopUpAsync).RequirePermission(OpsPermissions.Treasury.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryTopUp);
    }

    /// <summary>Lists the hot-pool wallets so the operator can see which one needs a top-up. The signing
    /// <c>KeyReference</c> is deliberately never returned (§10) — only the wallet id + address.</summary>
    private static async Task<IResult> HotPoolAsync(
        ITreasuryHotWalletDirectory hotWallets, IAssetCatalog assets, IBalanceReader balances, HttpContext http,
        string? chain = null)
    {
        // Nullable and checked here rather than declared required: a required minimal-API parameter that is
        // missing fails model binding before the handler runs, which produced a response with no envelope and
        // no error code for a caller to branch on.
        if (string.IsNullOrWhiteSpace(chain))
            return OpsResults.Bad(OpsErrorCodes.NetworkRequired, "A chain is required, e.g. ?chain=Tron.");

        if (!TryChain(chain, out var parsed))
            return BadChain(chain);

        var pool = await hotWallets.GetHotWalletPoolAsync(parsed, http.RequestAborted);
        var asset = await assets.FindAsync(parsed, "USDT", http.RequestAborted);

        var rows = new List<object>();
        foreach (var wallet in pool)
        {
            // Live on-chain balance, so an operator can see WHICH wallet is running dry and top up the one
            // that needs it. A balance that cannot be read is reported as null rather than zero: "unknown" and
            // "empty" call for opposite actions, and showing an unreadable wallet as empty would send someone
            // to top up a wallet that may be perfectly funded.
            decimal? available = null;
            string? availableBaseUnits = null;

            if (asset is not null)
            {
                try
                {
                    var balance = await balances.GetBalanceAsync(parsed, wallet.Address, asset.AssetId, http.RequestAborted);
                    available = AmountConversion.ToDisplay(balance, asset.Decimals);
                    availableBaseUnits = balance.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    // Leave both null — see above.
                }
            }

            rows.Add(new
            {
                wallet.WalletId,
                wallet.Address,
                coin = asset?.Symbol,
                available,
                availableBaseUnits,
            });
        }

        return Ok(rows);
    }

    /// <summary>
    /// Records company funds an admin has already moved into a hot withdrawal wallet. The amount is a display
    /// decimal at the edge (§14); the hash is verified on-chain before anything is recorded or posted, and the
    /// amount actually credited is the one the CHAIN shows, not the one typed.
    /// </summary>
    private static async Task<IResult> RecordTopUpAsync(
        RecordHotWalletTopUpRequest request,
        IAssetCatalog assets,
        IHotWalletTopUpService topUps,
        IAuditLogger audit,
        HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        var asset = await assets.FindAsync(parsed, "USDT", http.RequestAborted);
        if (asset is null)
            return OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"No USDT asset configured for {parsed}.");

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var baseUnits))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "Amount must be positive and within the asset's supported precision.");

        var actor = AuditActor.From(http);
        var result = await topUps.RecordAsync(
            new RecordTopUpRequest(
                parsed, asset.AssetId, request.TargetWalletId, baseUnits, request.TransactionHash.Trim(),
                request.SourceAddress?.Trim(), actor.Username),
            http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "treasury.top_up_recorded", "HotWalletTopUp",
            result.Value.TopUpId.ToString(), request.TransactionHash.Trim(), actor.IpAddress), http.RequestAborted);

        return Ok(new
        {
            topUpId = result.Value.TopUpId,
            targetAddress = result.Value.TargetAddress,
            // The verified on-chain amount, which may exceed what was typed — custody must reflect what
            // actually arrived, so this is what was booked.
            amount = AmountConversion.ToDisplay(result.Value.Amount, asset.Decimals),
            amountBaseUnits = result.Value.Amount.ToString(CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Every registered cold collection wallet, plus what each chain's two destinations currently are.
    ///
    /// <para>Chains are listed whether or not they have a destination: a chain with no Safe wallet is not
    /// sweeping at all, and a chain with no Danger wallet cannot sweep anything that screens badly — both are
    /// things a screen should say rather than leave to an absent row.</para>
    ///
    /// <para>Retired wallets are listed too, with their balances: they usually still hold funds, which is
    /// exactly why they stay in the custody audit.</para>
    /// </summary>
    private static async Task<IResult> ListColdAsync(
        ITreasuryColdWalletDirectory coldWallets, IAssetCatalog assets, IBalanceReader balances, HttpContext http)
    {
        var registered = await coldWallets.ListAsync(http.RequestAborted);

        var wallets = new List<object>(registered.Count);
        foreach (var wallet in registered)
        {
            var asset = await assets.FindAsync(wallet.Chain, "USDT", http.RequestAborted);

            // Live balance, with the same rule the hot pool uses: unreadable is reported as null, never as
            // zero. "Unknown" and "empty" call for opposite actions, and an empty-looking quarantine wallet
            // that is in fact full is the worse of the two mistakes.
            decimal? balance = null;
            string? balanceBaseUnits = null;
            if (asset is not null)
            {
                try
                {
                    var onChain = await balances.GetBalanceAsync(
                        wallet.Chain, wallet.Address, asset.AssetId, http.RequestAborted);
                    balance = AmountConversion.ToDisplay(onChain, asset.Decimals);
                    balanceBaseUnits = onChain.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    // Leave both null — see above.
                }
            }

            wallets.Add(new
            {
                walletId = wallet.WalletId,
                chain = wallet.Chain.ToString(),
                kind = wallet.Kind.ToString(),
                address = wallet.Address,
                label = wallet.Label,
                status = wallet.Status.ToString(),
                isActive = wallet.Status == ColdWalletStatus.Active,
                coin = asset?.Symbol,
                balance,
                balanceBaseUnits,
                screeningDecision = wallet.ScreeningDecision,
                screeningScore = wallet.ScreeningScore,
                screeningId = wallet.ScreeningId,
                screenedAt = wallet.ScreenedAt,
                createdAt = wallet.CreatedAt,
                updatedAt = wallet.UpdatedAt,
            });
        }

        var chains = Enum.GetValues<Chain>().Select(chain => new
        {
            chain = chain.ToString(),
            safe = Destination(registered, chain, ColdWalletKind.Safe),
            danger = Destination(registered, chain, ColdWalletKind.Danger),
        });

        return Ok(new { chains, wallets });
    }

    private static object? Destination(
        IReadOnlyList<RegisteredColdTreasuryWallet> registered, Chain chain, ColdWalletKind kind)
    {
        var wallet = registered.FirstOrDefault(
            w => w.Chain == chain && w.Kind == kind && w.Status == ColdWalletStatus.Active);

        return wallet is null
            ? null
            : new { walletId = wallet.WalletId, address = wallet.Address, label = wallet.Label };
    }

    /// <summary>
    /// Registers a cold collection address, optionally making it the destination straight away.
    ///
    /// <para>Screening never refuses one — the quarantine wallet is expected to score badly, and refusing a
    /// clean one on a vendor's say-so would leave a chain with nowhere to sweep. A non-clean verdict comes
    /// back in <c>warnings</c> and is recorded on the row, so a UI must not read a 200 as silence.</para>
    /// </summary>
    private static async Task<IResult> RegisterColdAsync(
        RegisterColdWalletRequest request, ITreasuryColdWalletRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        if (!TryKind(request.Kind, out var kind))
            return OpsResults.Bad(
                OpsErrorCodes.InvalidStatus,
                $"Unknown cold wallet kind '{request.Kind}'. Expected {nameof(ColdWalletKind.Safe)} or {nameof(ColdWalletKind.Danger)}.");

        var result = await registrar.RegisterAsync(
            new RegisterColdWalletCommand(parsed, kind, request.Address ?? string.Empty, request.Label, request.Activate),
            http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(
            audit, http, result.Value,
            result.Value.ReplacedAddress is null ? "treasury.cold_wallet_registered" : "treasury.cold_wallet_replaced",
            request.Reason);

        return Ok(Describe(result.Value, registered: true));
    }

    /// <summary>Points a chain's sweeps of one class at an already-registered wallet, retiring the one it
    /// replaces. Audited with both addresses — this is the highest-consequence action on the screen.</summary>
    private static async Task<IResult> ActivateColdAsync(
        Guid walletId, ColdWalletDesignationRequest? request, ITreasuryColdWalletRegistrar registrar,
        IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.ActivateAsync(walletId, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(audit, http, result.Value, "treasury.cold_wallet_activated", request?.Reason);
        return Ok(Describe(result.Value, registered: false));
    }

    /// <summary>Stops a wallet receiving sweeps. Refused for the one a chain is currently sweeping into —
    /// activating its replacement is what retires it, so a chain is never left with no destination.</summary>
    private static async Task<IResult> RetireColdAsync(
        Guid walletId, ColdWalletDesignationRequest? request, ITreasuryColdWalletRegistrar registrar,
        IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.RetireAsync(walletId, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(audit, http, result.Value, "treasury.cold_wallet_retired", request?.Reason);
        return Ok(Describe(result.Value, registered: false));
    }

    /// <summary>Screens the address again, ignoring the cache, and refreshes the verdict on the row. Spends
    /// provider quota, so it stays a deliberate staff action rather than something a screen does on load.</summary>
    private static async Task<IResult> ReScreenColdAsync(
        Guid walletId, ITreasuryColdWalletRegistrar registrar, HttpContext http)
    {
        var result = await registrar.ReScreenAsync(walletId, http.RequestAborted);
        return result.IsFailure ? OpsResults.Fail(result.Error!) : Ok(Describe(result.Value, registered: false));
    }

    private static async Task AuditAsync(
        IAuditLogger audit, HttpContext http, ColdWalletRegistration registration, string action, string? reason)
    {
        var actor = AuditActor.From(http);
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var detail = registration.ReplacedAddress is null
            ? $"{registration.Chain}/{registration.Kind} address={registration.Address}"
            : $"{registration.Chain}/{registration.Kind} from={registration.ReplacedAddress}; to={registration.Address}";

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, action, "TreasuryColdWallet", registration.WalletId.ToString(),
            Truncate(trimmedReason is null ? detail : $"{trimmedReason} ({detail})", 512), actor.IpAddress),
            http.RequestAborted);
    }

    private static object Describe(ColdWalletRegistration registration, bool registered) => new
    {
        walletId = registration.WalletId,
        chain = registration.Chain.ToString(),
        kind = registration.Kind.ToString(),
        address = registration.Address,
        label = registration.Label,
        status = registration.Status.ToString(),
        isActive = registration.Status == ColdWalletStatus.Active,
        replacedAddress = registration.ReplacedAddress,
        screeningDecision = registration.ScreeningDecision,
        screeningScore = registration.ScreeningScore,
        screeningId = registration.ScreeningId,
        // Empty for a clean or unscreened-by-choice address, so the normal case stays silent.
        warnings = registration.Warnings,
        registered,
    };

    private static bool TryKind(string? kind, out ColdWalletKind parsed)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            parsed = ColdWalletKind.Safe;
            return true;
        }

        return Enum.TryParse(kind, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool TryChain(string chain, out Chain parsed) => Enum.TryParse(chain, ignoreCase: true, out parsed);

    private static IResult BadChain(string chain) => OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{chain}'.");

    private static IResult Ok(object data) => OpsResults.Ok(data);
}
