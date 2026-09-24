using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Models;

public sealed class PortalLoginRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required, MaxLength(256)] public string Password { get; init; } = null!;

    /// <summary>
    /// The authenticator code, or a single-use recovery code. <b>This field now works</b> — it was accepted
    /// and ignored before 2FA existed, which the integration docs recorded as a gap.
    ///
    /// <para>Kept as <c>otp</c> so a client already sending it needs no change, while <see cref="Code"/> is
    /// the preferred name because the Ops host calls it that and one name across both hosts is worth more
    /// than tidiness here. Either is accepted; <see cref="Code"/> wins if both are present.</para>
    /// </summary>
    [MaxLength(32)] public string? Otp { get; init; }

    /// <summary>Preferred spelling, matching the Ops host's login. Same meaning as <see cref="Otp"/>.</summary>
    [MaxLength(32)] public string? Code { get; init; }

    /// <summary>Whichever the caller sent. A blank string counts as absent, so an empty form field does not
    /// read as "a code was supplied and it was wrong".</summary>
    public string? TwoFactorCode =>
        string.IsNullOrWhiteSpace(Code) ? (string.IsNullOrWhiteSpace(Otp) ? null : Otp) : Code;
}
