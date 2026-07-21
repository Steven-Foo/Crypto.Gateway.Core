using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

public static class OpsAuthEndpoints
{
    public static void MapOpsAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/auth/login", LoginAsync); // the one unauthenticated Ops endpoint
        app.MapPost("/api/v1/ops/auth/logout", LogoutAsync);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, IStaffAuthService auth, HttpContext http)
    {
        var result = await auth.LoginAsync(new LoginCommand(request.Username, request.Password), http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status401Unauthorized);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { token = result.Value.Token, expiresAt = result.Value.ExpiresAt, role = result.Value.Role.ToString() },
            error = (string?)null,
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IStaffAuthService auth)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : header;

        await auth.LogoutAsync(token, http.RequestAborted);
        return Results.Ok(new { isSuccess = true, data = new { loggedOut = true }, error = (string?)null });
    }
}
