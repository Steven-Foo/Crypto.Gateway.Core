using System.Net;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Api.OperationsApi.Services;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
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

        // Mutations — ops.merchants.manage (key rotation gets its own, more sensitive code).
        app.MapPost("/api/v1/ops/merchants", CreateMerchantAsync).RequirePermission(OpsPermissions.Merchants.Manage);
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
            error = (string?)null,
        });
    }

    private static async Task<IResult> GetMerchantAsync(Guid id, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.GetAsync(id, http.RequestAborted);
        return result.IsFailure
            ? Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, SetMerchantStatusRequest request, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = request.Active
            ? await registrar.ActivateAsync(id, http.RequestAborted)
            : await registrar.FreezeAsync(id, http.RequestAborted);

        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

        var view = await registrar.GetAsync(id, http.RequestAborted);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.status_changed", "Merchant", id.ToString(),
            $"status={view.Value.Status}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, status = view.Value.Status }, error = (string?)null });
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
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.status_changed", "Merchant", id.ToString(),
            "status=Closed", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, status = "Closed" }, error = (string?)null });
    }

    private static async Task<IResult> RegenerateKeyAsync(Guid id, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.RotateCredentialAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

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
            error = (string?)null,
        });
    }

    private static async Task<IResult> GetAllowedIpsAsync(Guid id, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.GetAsync(id, http.RequestAborted);
        return result.IsFailure
            ? Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new { isSuccess = true, data = new { merchantId = id, allowedIps = result.Value.AllowedIps }, error = (string?)null });
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
            return Results.Json(
                new { isSuccess = false, error = $"No valid IPs provided. Invalid: {string.Join(", ", invalidIps)}. Existing allowed IPs are unchanged." },
                statusCode: StatusCodes.Status400BadRequest);

        var result = await registrar.UpdateAllowedIpsAsync(id, validIps, http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

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
            error = (string?)null,
        });
    }

    private static async Task<IResult> CreateMerchantAsync(
        CreateMerchantRequest request, IMerchantRegistrar registrar, IDepositAddressProvisioner provisioner,
        IAuditLogger audit, ILogger<Program> logger, HttpContext http)
    {
        var result = await registrar.RegisterAsync(
            request.MerchantCode, request.Name, request.CallbackUrl, http.RequestAborted);

        if (result.IsFailure)
            return Results.Json(
                new { isSuccess = false, error = result.Error!.Message },
                statusCode: StatusCodes.Status400BadRequest);

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
            },
            error = (string?)null,
        });
    }
}
