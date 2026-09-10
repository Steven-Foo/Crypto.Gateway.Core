using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing per-merchant fee configuration — the write path a merchant's <c>flat + %</c> deposit and
/// withdrawal pricing was missing (until now every merchant was unpriced ⇒ zero fee). Amounts cross
/// display↔base-unit only here (§14); the fee math and all bounds live in the domain <c>FeeSchedule</c>
/// behind <see cref="IMerchantAssetPolicyService"/>. Setting a fee changes no money already moved — it prices
/// the merchant's <em>future</em> deposits and withdrawals.
/// </summary>
public static class OpsMerchantFeeEndpoints
{
    public static void MapOpsMerchantFeeApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/merchants/{id:guid}/fees", ListAsync).RequirePermission(OpsPermissions.Fees.View);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/fees", SetAsync).RequirePermission(OpsPermissions.Fees.Manage);
    }

    private static async Task<IResult> ListAsync(
        Guid id, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        var result = await policies.ListAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var rows = new List<object>(result.Value.Count);
        foreach (var p in result.Value)
        {
            var asset = await assets.FindByIdAsync(p.AssetId, http.RequestAborted);
            var decimals = asset?.Decimals ?? 6;
            rows.Add(new
            {
                assetId = p.AssetId,
                network = asset?.Chain.ToString(),
                coin = asset?.Symbol,
                depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.DepositFeeFixed), decimals),
                depositFeePercent = OpsPercent.ToPercent(p.DepositFeeBps),
                depositFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumDepositFee), decimals),
                withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.WithdrawalFee), decimals),
                withdrawalFeePercent = OpsPercent.ToPercent(p.WithdrawalFeeBps),
                withdrawalFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumWithdrawalFee), decimals),
                topUpFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.TopUpFeeFixed), decimals),
                topUpFeePercent = OpsPercent.ToPercent(p.TopUpFeeBps),

                // The policies the deposit-limits / withdrawal-limits / withdrawal-cap / approval-threshold
                // PUTs write. They were write-only until now: staff could set them and then had no screen
                // anywhere showing what a merchant's limits actually are, so every save was a blind overwrite
                // of an invisible value.
                //
                // NULL IS NOT ZERO here, and the distinction is load-bearing: null means "not configured, so
                // the platform config default applies", while 0 is a real configured value (a 0 threshold
                // means everything needs approval). Do not coalesce it.
                minimumDeposit = p.MinimumDeposit is { } minDep ? AmountConversion.ToDisplay(BigInteger.Parse(minDep), decimals) : (decimal?)null,
                maximumDeposit = p.MaximumDeposit is { } maxDep ? AmountConversion.ToDisplay(BigInteger.Parse(maxDep), decimals) : (decimal?)null,
                minimumWithdrawal = p.MinimumWithdrawal is { } minWd ? AmountConversion.ToDisplay(BigInteger.Parse(minWd), decimals) : (decimal?)null,
                maximumWithdrawal = p.MaximumWithdrawal is { } maxWd ? AmountConversion.ToDisplay(BigInteger.Parse(maxWd), decimals) : (decimal?)null,
                merchantWithdrawalCapFlat = p.MerchantWithdrawalFlatCap is { } capFlat ? AmountConversion.ToDisplay(BigInteger.Parse(capFlat), decimals) : (decimal?)null,
                merchantWithdrawalCapPercent = OpsPercent.ToPercent(p.MerchantWithdrawalPercentBps),
                approvalThreshold = p.ApprovalThreshold is { } thr ? AmountConversion.ToDisplay(BigInteger.Parse(thr), decimals) : (decimal?)null,
            });
        }

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, fees = rows }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> SetAsync(
        Guid id, SetMerchantFeeRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets,
        IAuditLogger audit, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // null stays null all the way to the service, where it means "leave the stored value alone" — see
        // SetMerchantFeeRequest. Only a supplied value is converted and validated; percent → bps happens here
        // and nowhere else, so bps never appears above this boundary.
        if (!OpsPercent.TryToBps(request.DepositFeePercent, out var depositBps))
            return Bad(OpsErrorCodes.InvalidAmount, "depositFeePercent must be non-negative, at most 100%, and at most 2 decimal places.");
        if (!OpsPercent.TryToBps(request.WithdrawalFeePercent, out var withdrawalBps))
            return Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeePercent must be non-negative, at most 100%, and at most 2 decimal places.");
        if (!OpsPercent.TryToBps(request.TopUpFeePercent, out var topUpBps))
            return Bad(OpsErrorCodes.InvalidAmount, "topUpFeePercent must be non-negative, at most 100%, and at most 2 decimal places.");

        if (!TryFeeToBase(request.DepositFeeFixed, asset.Decimals, out var depositFixed))
            return Bad(OpsErrorCodes.InvalidAmount, "depositFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.WithdrawalFeeFixed, asset.Decimals, out var withdrawalFixed))
            return Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.TopUpFeeFixed, asset.Decimals, out var topUpFixed))
            return Bad(OpsErrorCodes.InvalidAmount, "topUpFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.DepositFeeMinimum, asset.Decimals, out var depositMinimum))
            return Bad(OpsErrorCodes.InvalidAmount, "depositFeeMinimum is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.WithdrawalFeeMinimum, asset.Decimals, out var withdrawalMinimum))
            return Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeeMinimum is negative or finer than the asset's precision.");

        var result = await policies.SetFeesAsync(
            id, asset.AssetId, depositFixed, depositBps, withdrawalFixed, withdrawalBps,
            topUpFixed, topUpBps, depositMinimum, withdrawalMinimum,
            http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.fee_updated", "Merchant", id.ToString(),
            // "unchanged" for an omitted component, so the trail records what the operator actually asked for
            // rather than implying they set a zero they never sent.
            $"{asset.Symbol}: deposit={Describe(request.DepositFeeFixed, request.DepositFeePercent, request.DepositFeeMinimum)}, "
            + $"withdrawal={Describe(request.WithdrawalFeeFixed, request.WithdrawalFeePercent, request.WithdrawalFeeMinimum)}, "
            + $"topup={Describe(request.TopUpFeeFixed, request.TopUpFeePercent, minimum: null)}",
            actor.IpAddress), http.RequestAborted);

        // One read back, serving two purposes: the disproportionate-minimum warning below, and returning the
        // RESULTING pricing rather than an echo of the asset that was addressed. A save that changed something
        // the caller did not intend used to be invisible in the response; now the state after the write comes
        // back, so a client can see exactly what it left behind (§10.1 — the screen is a view of authoritative
        // state, not of what it hoped it sent).
        var after = await policies.ListAsync(id, http.RequestAborted);
        var saved = after.IsSuccess ? after.Value.SingleOrDefault(p => p.AssetId == asset.AssetId) : null;

        // Soft warning only — never blocks the save. Flags a minimum fee that looks disproportionate next to
        // this merchant's OWN configured minimum transaction amount (best-effort: doesn't chase the platform
        // config fallback when the merchant hasn't set one, since that's a per-chain, not per-merchant, number).
        // Compared against the STORED minimums, so an omitted ("unchanged") minimum is still evaluated.
        var warnings = new List<string>();
        if (saved is not null)
        {
            AddMinimumFeeWarning(warnings, "deposit", saved.MinimumDeposit, ParseBase(saved.MinimumDepositFee), asset.Decimals);
            AddMinimumFeeWarning(warnings, "withdrawal", saved.MinimumWithdrawal, ParseBase(saved.MinimumWithdrawalFee), asset.Decimals);
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = id,
                assetId = asset.AssetId,
                coin = asset.Symbol,
                network = chain.ToString(),
                // Percent on the wire, never bps (§ the host's standardization rule).
                fees = saved is null ? null : new
                {
                    depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(saved.DepositFeeFixed), asset.Decimals),
                    depositFeePercent = OpsPercent.ToPercent(saved.DepositFeeBps),
                    depositFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(saved.MinimumDepositFee), asset.Decimals),
                    withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(saved.WithdrawalFee), asset.Decimals),
                    withdrawalFeePercent = OpsPercent.ToPercent(saved.WithdrawalFeeBps),
                    withdrawalFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(saved.MinimumWithdrawalFee), asset.Decimals),
                    topUpFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(saved.TopUpFeeFixed), asset.Decimals),
                    topUpFeePercent = OpsPercent.ToPercent(saved.TopUpFeeBps),
                },
            },
            warnings,
            error = (string?)null, errorCode = (string?)null,
        });
    }

    /// <summary>A minimum fee at or above half of this merchant's own minimum transaction amount is flagged —
    /// not blocked — as likely-disproportionate pricing (e.g. a 5 USDT minimum fee on a 1 USDT minimum
    /// deposit would tax a customer's smallest allowed payment at 100%+). Silent when the merchant has no
    /// minimum amount of their own set (nothing concrete to compare against).</summary>
    private static void AddMinimumFeeWarning(List<string> warnings, string kind, string? minimumAmountBase, BigInteger minimumFee, int decimals)
    {
        if (minimumAmountBase is null || minimumFee <= BigInteger.Zero)
            return;

        var minimumAmount = BigInteger.Parse(minimumAmountBase, CultureInfo.InvariantCulture);
        if (minimumAmount <= BigInteger.Zero)
            return;

        if (minimumFee * 2 >= minimumAmount)
        {
            var feeDisplay = AmountConversion.ToDisplay(minimumFee, decimals);
            var amountDisplay = AmountConversion.ToDisplay(minimumAmount, decimals);
            warnings.Add(
                $"The {kind} minimum fee ({feeDisplay}) is at least half of this merchant's own minimum {kind} amount " +
                $"({amountDisplay}) — small transactions could be taxed disproportionately. Not blocked; review before relying on it.");
        }
    }

    /// <summary>Renders one requested schedule for the audit trail, distinguishing an omitted component
    /// ("unchanged") from a supplied zero. Percent, not bps — the trail should read the way the operator
    /// typed it. <paramref name="minimum"/> is null for top-up, which has no minimum-fee concept.</summary>
    private static string Describe(decimal? fixedFee, decimal? percent, decimal? minimum)
    {
        if (fixedFee is null && percent is null && minimum is null)
            return "unchanged";

        var rendered = $"{Part(fixedFee)}+{Part(percent)}%";
        return minimum is null ? rendered : $"{rendered}(min {Part(minimum)})";

        static string Part(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unchanged";
    }

    /// <summary>An exact base-unit integer string from the policy view → <see cref="BigInteger"/>. The view's
    /// fee components are non-null strings, so an unparseable value means corrupt state, not "unset" — it
    /// yields zero, which only suppresses a soft warning and can never alter money.</summary>
    private static BigInteger ParseBase(string baseUnits) =>
        BigInteger.TryParse(baseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : BigInteger.Zero;

    /// <summary>The fee fixed component: like <c>AmountConversion.TryToBaseUnits</c> but a zero is valid
    /// (a pure-percentage fee). Still refuses negatives and over-precision — never truncates money (§14).
    /// <para>A null input yields a null result and succeeds: the caller omitted the field, which means
    /// "unchanged", not "zero". Collapsing the two here would reintroduce the destructive-omission bug this
    /// signature exists to prevent.</para></summary>
    private static bool TryFeeToBase(decimal? display, int decimals, out BigInteger? baseUnits)
    {
        if (display is null)
        {
            baseUnits = null;
            return true;
        }

        if (display == 0m)
        {
            baseUnits = BigInteger.Zero;
            return true;
        }

        var ok = AmountConversion.TryToBaseUnits(display.Value, decimals, out var converted);
        baseUnits = ok ? converted : null;
        return ok;
    }

    private static IResult Bad(string errorCode, string message) => OpsResults.Bad(errorCode, message);
}
