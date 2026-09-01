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
/// Staff (Admin) actions for the three-tier custody COLD RELOAD (cold treasury → hot pool) — the production
/// counterpart of MerchantGateway's dev-only <c>/dev/treasury/*</c> helpers. Human-in-the-loop and <b>keyless</b>:
/// the ops host builds the <em>unsigned</em> treasury→hot transfer, the operator signs with the cold key
/// CLIENT-SIDE (never sent to any backend, §10), and the money host's <c>TreasuryReloadWorker</c> broadcasts +
/// confirms it. It posts <b>no ledger entry</b> — the reload moves funds between two platform-controlled
/// addresses, so total custody is unchanged (§14). Every route is Admin-only.
/// </summary>
public static class OpsTreasuryEndpoints
{
    public static void MapOpsTreasuryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/treasury/hot-pool", HotPoolAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/cold-wallet", RegisterColdAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/reload", InitiateReloadAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/reload/{reloadId:guid}/submit", SubmitReloadAsync).RequirePermission(OpsPermissions.Treasury.Manage);

        // Records company funds an admin has ALREADY moved into a hot wallet from a company wallet outside
        // platform custody. Distinct from the cold reload above: that one this system builds and broadcasts;
        // this one already happened and is only being recorded, after on-chain verification.
        app.MapPost("/api/v1/ops/treasury/top-up", RecordTopUpAsync).RequirePermission(OpsPermissions.Treasury.Manage);
    }

    /// <summary>Lists the hot-pool wallets so the operator can pick a reload target. The signing
    /// <c>KeyReference</c> is deliberately never returned (§10) — only the wallet id + address.</summary>
    private static async Task<IResult> HotPoolAsync(
        string chain, ITreasuryHotWalletDirectory hotWallets, IAssetCatalog assets, IBalanceReader balances, HttpContext http)
    {
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

    private static async Task<IResult> RegisterColdAsync(
        RegisterColdWalletRequest request, ITreasuryColdWalletRegistrar registrar, HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        var result = await registrar.RegisterAsync(parsed, request.Address, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Ok(new { chain = parsed.ToString(), address = request.Address, registered = true });
    }

    /// <summary>Builds the unsigned treasury→hot transfer for the operator to sign client-side. Returns the
    /// reload id + the unsigned transaction hex; no key is involved (§10).</summary>
    private static async Task<IResult> InitiateReloadAsync(
        InitiateReloadRequest request, IAssetCatalog assets, ITreasuryReloadService reloads, HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        // USDT-only first cut; the catalog gives the asset id + decimals for the §14 display→base conversion.
        var asset = await assets.FindAsync(parsed, "USDT", http.RequestAborted);
        if (asset is null)
            return OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"No USDT asset configured for {parsed}.");

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var baseUnits))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "Amount must be positive and within the asset's supported precision.");

        var result = await reloads.InitiateAsync(parsed, asset.AssetId, request.TargetWalletId, baseUnits, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Ok(new { reloadId = result.Value.ReloadId, unsignedTransactionHex = result.Value.UnsignedTransactionHex });
    }

    private static async Task<IResult> SubmitReloadAsync(
        Guid reloadId, SubmitReloadRequest request, ITreasuryReloadService reloads, HttpContext http)
    {
        byte[] signed;
        try { signed = Convert.FromHexString(request.SignedHex); }
        catch (FormatException) { return OpsResults.Bad(OpsErrorCodes.InvalidHex, "signedHex must be valid hex."); }

        var result = await reloads.SubmitSignedAsync(reloadId, signed, http.RequestAborted);
        return result.IsFailure ? OpsResults.Fail(result.Error!) : Ok(new { reloadId, submitted = true });
    }

    private static bool TryChain(string chain, out Chain parsed) => Enum.TryParse(chain, ignoreCase: true, out parsed);

    private static IResult BadChain(string chain) => OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{chain}'.");

    private static IResult Ok(object data) => OpsResults.Ok(data);
}
