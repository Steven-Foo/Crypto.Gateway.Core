using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class LoginRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required, MaxLength(256)] public string Password { get; init; } = null!;
}
