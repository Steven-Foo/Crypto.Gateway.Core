using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Models;

public sealed class PortalLoginRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required, MaxLength(256)] public string Password { get; init; } = null!;

    /// <summary>Accepted for forward-compatibility with the portal's 2FA field but IGNORED — 2FA is not
    /// implemented in the backend yet (a documented gap, like the admin portal's).</summary>
    public string? Otp { get; init; }
}
