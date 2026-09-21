using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

/// <summary>
/// Register a cold collection address for a chain — a watch-only address whose key the system never holds (a
/// human signs anything that leaves it, §10).
/// </summary>
public sealed class RegisterColdWalletRequest
{
    [Required] public string Chain { get; init; } = null!;
    [Required, MaxLength(128)] public string Address { get; init; } = null!;

    /// <summary>Which class of swept funds it collects: <c>Safe</c> (clean sweeps, the default) or
    /// <c>Danger</c> (the quarantine address flagged deposit addresses are swept into).</summary>
    public string? Kind { get; init; }

    /// <summary>The operator's own name for the address, so two cold addresses can be told apart on a screen
    /// without comparing base58 strings character by character.</summary>
    [MaxLength(128)] public string? Label { get; init; }

    /// <summary>
    /// Make it the destination immediately, retiring whichever wallet of the same kind holds that role.
    /// Defaults to false: adding an address and pointing a chain's sweeps at it are separate decisions, and
    /// conflating them means a mistyped address starts receiving funds the moment it is saved.
    /// </summary>
    public bool Activate { get; init; }

    /// <summary>Why the address is being registered or activated. Recorded on the audit entry: designating a
    /// destination redirects every future sweep of that class, so it must never be an unexplained change.</summary>
    [MaxLength(200)] public string? Reason { get; init; }
}

/// <summary>Change which registered cold collection wallet a chain sweeps into.</summary>
public sealed class ColdWalletDesignationRequest
{
    /// <summary>Why. Audited alongside the address that stopped receiving funds.</summary>
    [MaxLength(200)] public string? Reason { get; init; }
}

/// <summary>Re-tune one chain's sweep dials. Every field is required — see <c>SweepSettingsUpdate</c>.</summary>
public sealed class UpdateSweepSettingsRequest
{
    /// <summary>False pauses concentration for the chain: balances stay on deposit addresses, which the
    /// platform also controls, so nothing is at risk while it is off.</summary>
    public bool Enabled { get; init; }

    /// <summary>Exact base units (§14) — a decimal here would be a display value, and the threshold applies
    /// to every active asset on the chain rather than to one of them.</summary>
    [Required, MaxLength(40)] public string MinSweepAmountBaseUnits { get; init; } = null!;

    public int Confirmations { get; init; }

    public int ScanIntervalMinutes { get; init; }
}

/// <summary>
/// Record company funds already moved into a hot withdrawal wallet, so the automated payout pipeline stays
/// funded. <see cref="Amount"/> is a display decimal converted at the edge (§14); the verified on-chain amount
/// is what is actually booked. <see cref="SourceAddress"/> is the company wallet the funds came from — outside
/// platform custody, recorded for audit, deliberately unconstrained.
/// </summary>
public sealed class RecordHotWalletTopUpRequest
{
    [Required] public string Chain { get; init; } = null!;
    [Required] public Guid TargetWalletId { get; init; }
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; init; }
    [Required, MaxLength(128)] public string TransactionHash { get; init; } = null!;
    [MaxLength(128)] public string? SourceAddress { get; init; }
}
