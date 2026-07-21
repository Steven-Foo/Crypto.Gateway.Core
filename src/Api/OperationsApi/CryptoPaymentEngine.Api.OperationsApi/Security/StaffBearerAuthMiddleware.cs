using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Replaces the old shared <c>X-Ops-Api-Key</c> gate with real per-operator bearer sessions
/// (<see cref="IStaffSessionValidator"/>). Login itself, health, and swagger are the only unauthenticated
/// paths — everything else needs a valid, unexpired, unrevoked session.
/// </summary>
public sealed class StaffBearerAuthMiddleware(RequestDelegate next)
{
    public const string PrincipalItem = "StaffPrincipal";
    private const string LoginPath = "/api/v1/ops/auth/login";

    public async Task InvokeAsync(HttpContext context, IStaffSessionValidator validator)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!TryGetBearerToken(context, out var token))
        {
            await Fail(context, "Missing or invalid Authorization header. Expected 'Bearer <token>'.");
            return;
        }

        var result = await validator.ValidateAsync(token, context.RequestAborted);
        if (result.IsFailure)
        {
            await Fail(context, result.Error!.Message);
            return;
        }

        context.Items[PrincipalItem] = result.Value;
        await next(context);
    }

    private static bool TryGetBearerToken(HttpContext context, out string token)
    {
        token = string.Empty;
        if (!context.Request.Headers.TryGetValue("Authorization", out var header))
            return false;

        var value = header.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        token = value["Bearer ".Length..].Trim();
        return !string.IsNullOrEmpty(token);
    }

    private static Task Fail(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { isSuccess = false, error = message });
    }
}
