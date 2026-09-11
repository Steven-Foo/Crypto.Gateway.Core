using System.Net;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Api.OperationsApi.Services;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing merchant management. Creating a merchant seeds exactly ONE deposit wallet — enough that
/// the merchant's very first <c>/deposit</c> call doesn't pay the provisioning cost synchronously — not a
/// pre-minted pool (PaymentIntent's on-demand allocate-or-mint logic, unaffected by this, covers every
/// wallet after the first: it reuses a free one or mints a new one when none is free, so nothing here is
/// pre-creating addresses that may never see a deposit). A failed seed does not roll back the merchant —
/// it just means the first deposit call provisions synchronously instead, same as before this endpoint
/// touched wallets at all.
/// </summary>
public static class OpsMerchantEndpoints
{
    public static void MapOpsMerchantApi(this IEndpointRouteBuilder app)
    {
        // Reads — ops.merchants.view.
        app.MapGet("/api/v1/ops/merchants", ListMerchantsAsync).RequirePermission(OpsPermissions.Merchants.View);
        app.MapGet("/api/v1/ops/merchants/{id:guid}", GetMerchantAsync).RequirePermission(OpsPermissions.Merchants.View);
        app.MapGet("/api/v1/ops/merchants/{id:guid}/allowed-ips", GetAllowedIpsAsync).RequirePermission(OpsPermissions.Merchants.View);
        app.MapGet("/api/v1/ops/merchants/next-code", GetNextCodeAsync).RequirePermission(OpsPermissions.Merchants.View);

        // Mutations — ops.merchants.manage (key rotation gets its own, more sensitive code).
        app.MapPost("/api/v1/ops/merchants", CreateMerchantAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/profile", UpdateProfileAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPatch("/api/v1/ops/merchants/{id:guid}/status", SetStatusAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPost("/api/v1/ops/merchants/{id:guid}/close", CloseMerchantAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPost("/api/v1/ops/merchants/{id:guid}/regenerate-key", RegenerateKeyAsync).RequirePermission(OpsPermissions.Merchants.RotateKey);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/allowed-ips", UpdateAllowedIpsAsync).RequirePermission(OpsPermissions.Merchants.Manage);
    }

    private static async Task<IResult> ListMerchantsAsync(
        IMerchantRegistrar registrar, HttpContext http, int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        var (items, total) = await registrar.ListAsync(page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    /// <summary>
    /// <c>data</c> stays exactly the existing <see cref="MerchantAdminView"/> shape — an already-documented,
    /// deployed frontend contract — so <c>balances</c> is added as its own sibling field alongside it rather
    /// than nesting <c>data</c> under a new wrapper.
    /// </summary>
    private static async Task<IResult> GetMerchantAsync(
        Guid id, IMerchantRegistrar registrar, ILedgerQuery ledger, IAssetCatalog assets, HttpContext http)
    {
        var result = await registrar.GetAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var activeAssets = await assets.GetActiveAsync(http.RequestAborted);
        var balances = new List<object>(activeAssets.Count);
        foreach (var asset in activeAssets)
        {
            var balance = await ledger.GetMerchantBalanceAsync(id, asset.AssetId, http.RequestAborted);
            balances.Add(new
            {
                assetId = asset.AssetId,
                network = asset.Chain.ToString(),
                coin = asset.Symbol,
                balance = AmountConversion.ToDisplay(balance, asset.Decimals),
                balanceBaseUnits = balance.ToString(),
            });
        }

        return Results.Ok(new { isSuccess = true, data = result.Value, balances, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, SetMerchantStatusRequest request, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = request.Active
            ? await registrar.ActivateAsync(id, http.RequestAborted)
            : await registrar.FreezeAsync(id, http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var view = await registrar.GetAsync(id, http.RequestAborted);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.status_changed", "Merchant", id.ToString(),
            $"status={view.Value.Status}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, status = view.Value.Status }, error = (string?)null, errorCode = (string?)null });
    }

    /// <summary>
    /// Closes a merchant (from Active or Frozen). Kept as its own endpoint rather than folded into
    /// <see cref="SetStatusAsync"/>'s boolean <c>active</c> field — that field is already a documented,
    /// deployed frontend contract (§ docs/backoffice-frontend-integration.md), and "close" is a more
    /// consequential action than the freeze/unfreeze toggle, so it gets an explicit, unambiguous route.
    /// Reversible — <c>PATCH .../status</c> with <c>active: true</c> or <c>active: false</c> reopens a
    /// closed merchant back to Active or Frozen respectively (status is never terminal, only every OTHER
    /// business operation independently keeps rejecting a Closed merchant).
    /// </summary>
    private static async Task<IResult> CloseMerchantAsync(Guid id, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.CloseAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.status_changed", "Merchant", id.ToString(),
            "status=Closed", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, status = "Closed" }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> RegenerateKeyAsync(Guid id, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.RotateCredentialAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.key_rotated", "Merchant", id.ToString(), null, actor.IpAddress),
            http.RequestAborted);

        var credential = result.Value;
        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                apiKey = credential.ApiKey,
                apiSecret = credential.ApiSecret,
                signingSecret = credential.SigningSecret,
                warning = "Store both values securely — they will never be shown again. The previous credential is now revoked.",
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    /// <summary>
    /// Preview of the code <c>POST .../merchants</c> will most likely mint next (e.g. "ME00042") — for the
    /// create-merchant form to display before submission. NOT a reservation: a concurrent create can still
    /// claim this exact number first, in which case the real generator silently rolls to the next one, same
    /// as it always has. Refresh this right before showing the create form, not once and cached.
    /// </summary>
    private static async Task<IResult> GetNextCodeAsync(IMerchantRegistrar registrar, HttpContext http)
    {
        var nextCode = await registrar.PreviewNextMerchantCodeAsync(http.RequestAborted);
        return Results.Ok(new { isSuccess = true, data = new { nextMerchantCode = nextCode }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> GetAllowedIpsAsync(Guid id, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.GetAsync(id, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { merchantId = id, allowedIps = result.Value.AllowedIps }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> UpdateAllowedIpsAsync(
        Guid id, UpdateAllowedIpsRequest request, IMerchantRegistrar registrar, IMerchantRepository repository,
        CloudflareService cloudflare, IAuditLogger audit, HttpContext http)
    {
        var invalidIps = new List<string>();
        var validIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in request.IpAddresses.Select(ip => ip.Trim()).Where(ip => !string.IsNullOrEmpty(ip)))
        {
            if (IPAddress.TryParse(raw, out _))
                validIps.Add(raw);
            else
                invalidIps.Add(raw);
        }

        // If every submitted IP was invalid and the request wasn't intentionally empty, keep existing IPs.
        if (validIps.Count == 0 && invalidIps.Count > 0)
            return OpsResults.Bad(OpsErrorCodes.InvalidIpAddress, $"No valid IPs provided. Invalid: {string.Join(", ", invalidIps)}. Existing allowed IPs are unchanged.");

        var result = await registrar.UpdateAllowedIpsAsync(id, validIps, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var change = result.Value;

        // Skip pushing to Cloudflare for any IP a different merchant still needs.
        var otherIps = (await repository.GetAllAllowedIpsExceptAsync(id, http.RequestAborted))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in change.Added.Where(ip => !otherIps.Contains(ip)))
            await cloudflare.AddIpAsync(ip, $"Merchant: {id}", http.RequestAborted);

        foreach (var ip in change.Removed.Where(ip => !otherIps.Contains(ip)))
            await cloudflare.RemoveIpAsync(ip, http.RequestAborted);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.allowed_ips_updated", "Merchant", id.ToString(),
            $"+[{string.Join(',', change.Added)}] -[{string.Join(',', change.Removed)}]", actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = id,
                allowedIps = change.Current,
                invalidIps,
                cloudflare = new { added = change.Added.Count, removed = change.Removed.Count },
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> CreateMerchantAsync(
        CreateMerchantRequest request, IMerchantRegistrar registrar, IDepositAddressProvisioner provisioner,
        IMerchantAssetPolicyService policies, IAssetCatalog assets,
        IMerchantAccountService portalAccounts, IMerchantRoleService portalRoles,
        IAuditLogger audit, ILogger<Program> logger, HttpContext http)
    {
        if (!TryParseSettlementMode(request.SettlementMode, out var settlementMode))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, $"Unknown settlementMode '{request.SettlementMode}'. Use 'auto' or 'manual'.");

        // Only TRX/USDT is priceable today (§CLAUDE.md — no BSC/ETH adapter exists yet). Resolved up front so a
        // Fees block on an unsupported asset is rejected outright, before the merchant is even created.
        AssetDto? asset = null;
        if (request.Fees is not null)
        {
            asset = await assets.FindAsync(Chain.Tron, "USDT", http.RequestAborted);
            if (asset is null)
                return OpsResults.Bad(OpsErrorCodes.InvalidAsset, "No priceable asset is configured (expected USDT on Tron).");

            if (!OpsPercent.TryToBps(request.Fees.DepositFeePercent, out var depositBpsCheck)
                || !OpsPercent.TryToBps(request.Fees.WithdrawalFeePercent, out var withdrawalBpsCheck))
                return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "Fee percent must be non-negative, at most 100%, and at most 2 decimal places.");
            _ = depositBpsCheck; _ = withdrawalBpsCheck; // validated here; recomputed below once the merchant exists
        }

        var result = await registrar.RegisterAsync(
            request.Name, callbackUrl: null, http.RequestAborted,
            contactEmail: request.ContactEmail, remark: request.Remark,
            settlementMode: settlementMode, settlementDelayDays: request.SettlementDays);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var merchant = result.Value;

        // Registration already leaves the merchant Active (no separate approval step, §MerchantStatus) — the
        // seed wallet below needs that (WalletProvisioningService gates on merchant.CanTransact).
        object? wallet = null;
        var provisioned = await provisioner.ProvisionDepositAddressAsync(merchant.MerchantId, Chain.Tron, http.RequestAborted);
        if (provisioned.IsFailure)
            logger.LogWarning(
                "Seed wallet failed to provision for merchant {MerchantId}: {Error}. The merchant's first " +
                "deposit call will provision one synchronously instead.", merchant.MerchantId, provisioned.Error!.Code);
        else
            wallet = new { chain = provisioned.Value.Chain.ToString(), address = provisioned.Value.Address };

        // A failure here does NOT roll back the merchant — same non-blocking philosophy as the seed wallet
        // above. Staff can always price the merchant afterward via PUT .../fees; a failed pricing call at
        // creation just means the merchant is briefly unpriced (falls back to the platform default fee).
        if (request.Fees is { } fees && asset is not null)
        {
            OpsPercent.TryToBps(fees.DepositFeePercent, out var depositBps);
            OpsPercent.TryToBps(fees.WithdrawalFeePercent, out var withdrawalBps);

            if (!TryFeeToBase(fees.DepositFeeFixed, asset.Decimals, out var depositFixed)
                || !TryFeeToBase(fees.WithdrawalFeeFixed, asset.Decimals, out var withdrawalFixed)
                || !TryFeeToBase(fees.DepositFeeMinimum, asset.Decimals, out var depositMinimum)
                || !TryFeeToBase(fees.WithdrawalFeeMinimum, asset.Decimals, out var withdrawalMinimum))
            {
                logger.LogWarning(
                    "Initial fee for merchant {MerchantId} was invalid (negative or over-precise) and was skipped; " +
                    "the merchant is unpriced until staff set it via PUT .../fees.", merchant.MerchantId);
            }
            else
            {
                var priced = await policies.SetFeesAsync(
                    merchant.MerchantId, asset.AssetId, depositFixed, depositBps, withdrawalFixed, withdrawalBps,
                    minimumDepositFee: depositMinimum, minimumWithdrawalFee: withdrawalMinimum,
                    cancellationToken: http.RequestAborted);

                if (priced.IsFailure)
                    logger.LogWarning(
                        "Initial fee for merchant {MerchantId} failed to save: {Error}. The merchant is unpriced " +
                        "until staff set it via PUT .../fees.", merchant.MerchantId, priced.Error!.Code);
            }
        }

        // Same non-blocking philosophy as the seed wallet and initial fee above — a failure here does NOT roll
        // back the merchant. Staff can always retry via POST .../portal-account afterward.
        object? portalAccount = null;
        var portalResult = await OpsMerchantPortalAccountEndpoints.ProvisionFirstPortalAccountAsync(
            merchant.MerchantId, merchant.MerchantCode, request.Name, portalAccounts, portalRoles, http.RequestAborted);
        if (portalResult.IsFailure)
            logger.LogWarning(
                "Portal account failed to provision for merchant {MerchantId}: {Error}. Staff can retry via " +
                "POST .../portal-account.", merchant.MerchantId, portalResult.Error!.Code);
        else
            portalAccount = new { username = portalResult.Value.Username, temporaryPassword = portalResult.Value.TemporaryPassword };

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.created", "Merchant", merchant.MerchantId.ToString(),
            $"code={merchant.MerchantCode}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = merchant.MerchantId,
                merchantCode = merchant.MerchantCode,
                apiKey = merchant.ApiKey,
                apiSecret = merchant.ApiSecret,
                signingSecret = merchant.SigningSecret,
                wallet,
                portalAccount,
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> UpdateProfileAsync(
        Guid id, UpdateMerchantProfileRequest request, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        SettlementMode? settlementMode = null;
        if (request.SettlementMode is not null)
        {
            if (!TryParseSettlementMode(request.SettlementMode, out var mode))
                return OpsResults.Bad(OpsErrorCodes.InvalidAmount, $"Unknown settlementMode '{request.SettlementMode}'. Use 'auto' or 'manual'.");
            settlementMode = mode;
        }

        var result = await registrar.SetProfileAsync(id, request.ContactEmail, settlementMode, request.Remark, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.profile_updated", "Merchant", id.ToString(), null, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id }, error = (string?)null, errorCode = (string?)null });
    }

    private static bool TryParseSettlementMode(string? value, out SettlementMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = SettlementMode.Manual;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out mode);
    }

    /// <summary>The fee fixed/minimum component: like <c>AmountConversion.TryToBaseUnits</c> but a zero is
    /// valid. Still refuses negatives and over-precision — never truncates money (§14).</summary>
    private static bool TryFeeToBase(decimal display, int decimals, out BigInteger baseUnits)
    {
        if (display == 0m)
        {
            baseUnits = BigInteger.Zero;
            return true;
        }

        return AmountConversion.TryToBaseUnits(display, decimals, out baseUnits);
    }
}
