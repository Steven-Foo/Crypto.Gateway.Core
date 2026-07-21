using System.Net;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Api.OperationsApi.Services;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing merchant management. Creating a merchant also eagerly provisions a fixed-size pool of
/// deposit wallets (matches the proven APIGateway design: pre-create N wallets so the deposit flow always
/// has a free address ready — the reuse/overflow-mint logic already lives in PaymentIntent and is
/// unaffected by whether a wallet was pre-created or minted on demand). Pool size is configurable
/// (<c>Operations:WalletPoolSize</c>, default 10). A single wallet failing to provision does not roll back
/// the merchant — it's logged loudly so ops can top up the pool manually rather than losing the whole
/// merchant over one bad derivation.
/// </summary>
public static class OpsMerchantEndpoints
{
    private const int DefaultWalletPoolSize = 10;

    public static void MapOpsMerchantApi(this IEndpointRouteBuilder app)
    {
        // Reads — any authenticated staff (Admin or Viewer).
        app.MapGet("/api/v1/ops/merchants", ListMerchantsAsync);
        app.MapGet("/api/v1/ops/merchants/{id:guid}", GetMerchantAsync);
        app.MapGet("/api/v1/ops/merchants/{id:guid}/allowed-ips", GetAllowedIpsAsync);

        // Mutations — Admin only.
        app.MapPost("/api/v1/ops/merchants", CreateMerchantAsync).RequireAdmin();
        app.MapPatch("/api/v1/ops/merchants/{id:guid}/status", SetStatusAsync).RequireAdmin();
        app.MapPost("/api/v1/ops/merchants/{id:guid}/regenerate-key", RegenerateKeyAsync).RequireAdmin();
        app.MapPut("/api/v1/ops/merchants/{id:guid}/allowed-ips", UpdateAllowedIpsAsync).RequireAdmin();
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
        Guid id, SetMerchantStatusRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = request.Active
            ? await registrar.ActivateAsync(id, http.RequestAborted)
            : await registrar.SuspendAsync(id, http.RequestAborted);

        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

        var view = await registrar.GetAsync(id, http.RequestAborted);
        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, status = view.Value.Status }, error = (string?)null });
    }

    private static async Task<IResult> RegenerateKeyAsync(Guid id, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.RotateCredentialAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status400BadRequest);

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
        CloudflareService cloudflare, HttpContext http)
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
        CreateMerchantRequest request,
        IMerchantRegistrar registrar,
        IDepositAddressProvisioner provisioner,
        IConfiguration configuration,
        ILogger<Program> logger,
        HttpContext http)
    {
        var result = await registrar.RegisterAsync(
            request.MerchantCode, request.Name, request.CallbackUrl, http.RequestAborted);

        if (result.IsFailure)
            return Results.Json(
                new { isSuccess = false, error = result.Error!.Message },
                statusCode: StatusCodes.Status400BadRequest);

        var merchant = result.Value;

        // Registration leaves a merchant Pending (a real onboarding-review gate) — staff creating one via
        // this endpoint IS the approval, so activate immediately or wallet provisioning below would refuse
        // (WalletProvisioningService gates on merchant.CanTransact).
        var activation = await registrar.ActivateAsync(merchant.MerchantId, http.RequestAborted);
        if (activation.IsFailure)
            return Results.Json(
                new { isSuccess = false, error = activation.Error!.Message },
                statusCode: StatusCodes.Status500InternalServerError);

        var poolSize = configuration.GetValue<int?>("Operations:WalletPoolSize") ?? DefaultWalletPoolSize;

        var wallets = new List<object>();
        for (var i = 0; i < poolSize; i++)
        {
            var provisioned = await provisioner.ProvisionDepositAddressAsync(
                merchant.MerchantId, Chain.Tron, http.RequestAborted);

            if (provisioned.IsFailure)
            {
                logger.LogWarning(
                    "Wallet {Index}/{PoolSize} failed to provision for merchant {MerchantId}: {Error}. " +
                    "The pool is short by this one — top it up manually.",
                    i + 1, poolSize, merchant.MerchantId, provisioned.Error!.Code);
                continue;
            }

            wallets.Add(new { chain = provisioned.Value.Chain.ToString(), address = provisioned.Value.Address });
        }

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
                wallets,
            },
            error = (string?)null,
        });
    }
}
