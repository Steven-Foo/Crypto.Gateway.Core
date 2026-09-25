using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class CreateAccountRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [Required] public Guid RoleId { get; init; }

    /// <summary>The live 2FA switch for this account (§ StaffUser.RequireTwoFactor) — true forces the new
    /// account through enrollment on its first login, false lets it use the back office without 2FA until an
    /// admin turns the switch on. Defaults true (today's universal-force behaviour) when the caller omits it,
    /// so an older client never silently creates an unprotected account.</summary>
    public bool RequireTwoFactor { get; init; } = true;
}

public sealed class SetAccountStatusRequest
{
    [Required] public bool Active { get; init; }
}

public sealed class ChangeAccountRoleRequest
{
    [Required] public Guid RoleId { get; init; }
}

public sealed class SetAccountRequireTwoFactorRequest
{
    [Required] public bool RequireTwoFactor { get; init; }
}
