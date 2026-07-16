using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Minimal shared-secret auth for staff-facing operations endpoints — deliberately NOT the merchant-facing
/// HMAC scheme, since this is a different trust model (internal staff, not external partners). This is a
/// stopgap: real per-operator accounts + RBAC (roles, permissions, audit trail) is separate, larger design
/// work — same as APIGateway's Bo project grew into over time. Do not expose this host to the public
/// internet without that follow-up landing first.
/// </summary>
public sealed class OpsApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var configuredKey = configuration["Operations:ApiKey"];
        if (string.IsNullOrEmpty(configuredKey))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Operations:ApiKey is not configured." });
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Ops-Api-Key", out var provided) ||
            !FixedTimeEquals(provided.ToString(), configuredKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Ops-Api-Key." });
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
