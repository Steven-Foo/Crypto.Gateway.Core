using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class CreateAccountRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required] public Guid RoleId { get; init; }
}

public sealed class SetAccountStatusRequest
{
    [Required] public bool Active { get; init; }
}

public sealed class ChangeAccountRoleRequest
{
    [Required] public Guid RoleId { get; init; }
}
