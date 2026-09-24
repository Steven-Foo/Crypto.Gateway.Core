using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class LoginRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required, MaxLength(256)] public string Password { get; init; } = null!;

    /// <summary>
    /// The authenticator code, or a single-use recovery code. Optional in the SHAPE only: an enrolled
    /// account is refused without it (<c>two_factor.code_required</c>). It is nullable because an account
    /// that has not finished enrolling has no code to give, and blocking its login would leave it
    /// permanently unable to enroll.
    /// </summary>
    [MaxLength(32)] public string? Code { get; init; }
}
