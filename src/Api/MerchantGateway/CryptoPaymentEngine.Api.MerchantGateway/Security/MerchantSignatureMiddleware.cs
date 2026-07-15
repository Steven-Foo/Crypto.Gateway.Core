using System.Text;
using CryptoPaymentEngine.Api.MerchantGateway.Models;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

namespace CryptoPaymentEngine.Api.MerchantGateway.Security;

/// <summary>
/// Enforces the partner's frozen request-signing on the merchant API (<c>/api/v1</c>): <c>X-Api-Key</c> +
/// <c>X-Timestamp</c> + <c>X-Signature</c> = HMAC-SHA256 over <c>"{timestamp}\n{body}"</c>. The HMAC and key
/// resolution are delegated to the Merchant module (<see cref="IMerchantRequestVerifier"/>) so the signing
/// secret never leaves it (§10); this middleware owns only the transport concerns — header presence, the
/// 5-minute replay window, and placing the resolved merchant id in <see cref="HttpContext.Items"/>.
/// </summary>
public sealed class MerchantSignatureMiddleware(RequestDelegate next)
{
    public const string MerchantIdItem = "MerchantId";
    private const int ReplayWindowSeconds = 300;

    public async Task InvokeAsync(HttpContext context, IMerchantRequestVerifier verifier)
    {
        // Guard only the merchant API surface; the pay page, health, and swagger pass through unauthenticated.
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        if (!TryHeader(context, "X-Api-Key", out var apiKey))
        {
            await WriteFail(context, StatusCodes.Status401Unauthorized, "Missing API key.");
            return;
        }

        if (!TryHeader(context, "X-Timestamp", out var timestamp) || !TryHeader(context, "X-Signature", out var signature))
        {
            await WriteFail(context, StatusCodes.Status400BadRequest, "Missing X-Timestamp or X-Signature header.");
            return;
        }

        if (!long.TryParse(timestamp, out var unixSeconds))
        {
            await WriteFail(context, StatusCodes.Status400BadRequest, "Invalid X-Timestamp value.");
            return;
        }

        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > ReplayWindowSeconds)
        {
            await WriteFail(context, StatusCodes.Status401Unauthorized, "Request timestamp expired. Resend within 5 minutes.");
            return;
        }

        var body = await ReadBufferedBodyAsync(context);

        var result = await verifier.VerifyAsync(apiKey, timestamp, body, signature, context.RequestAborted);
        if (result.IsFailure)
        {
            await WriteFail(context, StatusCodes.Status401Unauthorized, result.Error!.Message);
            return;
        }

        context.Items[MerchantIdItem] = result.Value;
        await next(context);
    }

    /// <summary>Reads the body without consuming it, so model binding can read it again downstream.</summary>
    private static async Task<string> ReadBufferedBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        return body;
    }

    private static bool TryHeader(HttpContext context, string name, out string value)
    {
        value = context.Request.Headers[name].FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static Task WriteFail(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(ApiResponse.Fail(message));
    }
}
