using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class CreateRoleRequest
{
    [Required, MaxLength(64)] public string Name { get; init; } = null!;
    [MaxLength(256)] public string? Description { get; init; }
    public List<string> PermissionCodes { get; init; } = [];
}

public sealed class UpdateRoleDetailsRequest
{
    [Required, MaxLength(64)] public string Name { get; init; } = null!;
    [MaxLength(256)] public string? Description { get; init; }
}

public sealed class SetRolePermissionsRequest
{
    [Required] public List<string> PermissionCodes { get; init; } = [];
}
