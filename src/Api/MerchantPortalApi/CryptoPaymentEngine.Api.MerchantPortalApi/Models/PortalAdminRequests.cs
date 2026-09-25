using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Models;

// ── accounts ──

public sealed class CreatePortalAccountRequest
{
    [Required, MaxLength(64)] public string Username { get; init; } = null!;
    [MaxLength(128)] public string? DisplayName { get; init; }

    /// <summary>The role to grant. Optional: an account created without one has NO permissions until a role is
    /// assigned (fail-closed).</summary>
    public Guid? RoleId { get; init; }

    /// <summary>The live 2FA switch for this account (§ MerchantUser.RequireTwoFactor) — true forces it
    /// through enrollment on first login, false lets it use the portal without 2FA until an admin turns the
    /// switch on. Defaults true (today's universal-force behaviour) when the caller omits it.</summary>
    public bool RequireTwoFactor { get; init; } = true;
}

public sealed class SetPortalAccountStatusRequest
{
    public bool Active { get; init; }
}

public sealed class SetPortalAccountRequireTwoFactorRequest
{
    public bool RequireTwoFactor { get; init; }
}

public sealed class AssignPortalRoleRequest
{
    /// <summary>Null clears the role, leaving the account with no permissions.</summary>
    public Guid? RoleId { get; init; }
}

public sealed class ChangeOwnPasswordRequest
{
    [Required, MaxLength(256)] public string CurrentPassword { get; init; } = null!;
    [Required, MaxLength(256)] public string NewPassword { get; init; } = null!;
}

// ── roles ──

public sealed class CreatePortalRoleRequest
{
    [Required, MaxLength(64)] public string Name { get; init; } = null!;
    [MaxLength(256)] public string? Description { get; init; }
    public string[] PermissionCodes { get; init; } = [];
}

public sealed class UpdatePortalRoleRequest
{
    [Required, MaxLength(64)] public string Name { get; init; } = null!;
    [MaxLength(256)] public string? Description { get; init; }
}

public sealed class SetPortalRolePermissionsRequest
{
    public string[] PermissionCodes { get; init; } = [];
}

// ── API credential ──

public sealed class UpdateAllowedIpsRequest
{
    /// <summary>The complete replacement allowlist (not a delta): single full IP addresses, no CIDR ranges. An empty
    /// array clears it, which BLOCKS every API call, the same semantics the Ops endpoint has.</summary>
    public string[] AllowedIps { get; init; } = [];
}

// ── money-out ──

public sealed class CreatePortalPayoutRequest
{
    [Required, MaxLength(64)] public string MerchantOrderNumber { get; init; } = null!;
    [Required, MaxLength(16)] public string Network { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    [Required, MaxLength(128)] public string ReceivingAddress { get; init; } = null!;

    /// <summary>Display units (e.g. 12.50 USDT) — converted to base units at this edge, refusing over-precision
    /// rather than truncating (§14).</summary>
    public decimal Amount { get; init; }
}

public sealed class CreatePortalCashOutRequest
{
    [Required, MaxLength(64)] public string MerchantOrderNumber { get; init; } = null!;
    [Required, MaxLength(16)] public string Network { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;

    /// <summary>Display units. No destination: it is resolved from the platform-whitelisted settlement wallet,
    /// never supplied by the caller (§10).</summary>
    public decimal Amount { get; init; }
}

public sealed class RejectPortalPayoutRequest
{
    /// <summary>Why the merchant's approver declined it — recorded on the withdrawal for both sides to see.
    /// Optional; a default is recorded if omitted.</summary>
    [MaxLength(512)] public string? Reason { get; init; }
}

/// <summary>
/// Creates a top-up invoice — the merchant funding its OWN balance by sending crypto to the address this
/// returns. Distinct from a customer payment invoice: the merchant is the payer, so <see cref="Amount"/> is
/// exactly what it will send (never grossed up), and it is credited that minus the merchant's top-up fee
/// (zero unless platform staff declared one).
/// </summary>
public sealed class CreatePortalTopUpRequest
{
    /// <summary>The merchant's own reference for this top-up. Unique per merchant — a reused value is
    /// rejected as a duplicate rather than silently returning the earlier invoice.</summary>
    [Required, MaxLength(64)] public string MerchantOrderNumber { get; init; } = null!;

    [Required, MaxLength(16)] public string Network { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;

    /// <summary>Display units — the exact amount to send. Converted to base units at this edge, refusing
    /// over-precision rather than truncating (§14).</summary>
    public decimal Amount { get; init; }
}
