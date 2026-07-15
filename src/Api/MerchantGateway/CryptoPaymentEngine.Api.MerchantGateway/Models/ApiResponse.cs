namespace CryptoPaymentEngine.Api.MerchantGateway.Models;

/// <summary>
/// The frozen merchant-facing response envelope — byte-shape-identical to the partner's
/// <c>Shared.Models.ApiResponse</c>, so their SDKs and frontend integrate unchanged. This is the one
/// place a <c>Result</c> becomes an HTTP body; nothing internal leaks through it.
/// </summary>
public sealed class ApiResponse
{
    public bool IsSuccess { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }

    public static ApiResponse Ok(object? data = null) => new() { IsSuccess = true, Data = data };

    public static ApiResponse Fail(string error) => new() { IsSuccess = false, Error = error };
}
