using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

/// <summary>
/// A cold collection wallet: a watch-only address the platform sweeps deposits into, whose key is NOT held
/// by the system (a human signs anything that leaves it, §10). Watch-only by nature — nothing here derives
/// from it or signs with it — so it is homed in Treasury rather than forced into the Wallet module's
/// key-bearing platform-wallet model.
///
/// <para>A chain has one <em>active</em> wallet per <see cref="ColdWalletKind"/>: clean sweeps land in the
/// Safe wallet, flagged ones in the Danger wallet. Replacing a destination is an <b>add-and-activate</b>,
/// never an edit of the address on an existing row — the old address usually still holds funds, and
/// rewriting it would silently drop those funds from the custody total and surface as a shortfall in
/// reconciliation. Retired rows are kept forever for the same reason.</para>
/// </summary>
public sealed class TreasuryColdWallet : Entity<Guid>
{
    private TreasuryColdWallet(
        Guid id, Chain chain, ColdWalletKind kind, string address, string? label,
        ColdWalletStatus status, DateTimeOffset now) : base(id)
    {
        Chain = chain;
        Kind = kind;
        Address = address;
        Label = label;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private TreasuryColdWallet() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }

    /// <summary>Which class of swept funds this wallet collects. Fixed at registration: a wallet that has
    /// received tainted funds can never become the clean destination, because the funds already sitting in
    /// it do not change class.</summary>
    public ColdWalletKind Kind { get; private set; }

    public string Address { get; private set; } = null!;

    /// <summary>The operator's own name for the address ("Ledger Nano #2 — cold A"), so staff can tell two
    /// addresses apart without comparing base58 strings character by character.</summary>
    public string? Label { get; private set; }

    public ColdWalletStatus Status { get; private set; }

    /// <summary>The last screening recorded for this address. Evidence for staff, never a gate — see
    /// <see cref="ColdWalletKind.Danger"/> for why a bad verdict must not disqualify a collection wallet.</summary>
    public Guid? ScreeningId { get; private set; }

    public string? ScreeningDecision { get; private set; }
    public int? ScreeningScore { get; private set; }
    public DateTimeOffset? ScreenedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == ColdWalletStatus.Active;

    /// <summary>
    /// Registers a new collection wallet. It starts <see cref="ColdWalletStatus.Retired"/> unless
    /// <paramref name="activate"/> is set — adding an address and making it the live sweep destination are
    /// separate decisions, and conflating them means a mistyped address starts receiving funds the moment
    /// it is saved.
    /// </summary>
    public static Result<TreasuryColdWallet> Register(
        Chain chain, ColdWalletKind kind, string address, string? label, bool activate, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<TreasuryColdWallet>(TreasuryColdWalletErrors.AddressRequired);

        var trimmedLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

        return Result.Success(new TreasuryColdWallet(
            Guid.CreateVersion7(), chain, kind, address.Trim(), trimmedLabel,
            activate ? ColdWalletStatus.Active : ColdWalletStatus.Retired, now));
    }

    /// <summary>Makes this wallet the destination for its (chain, kind). The caller retires the one it
    /// replaces in the same transaction — the filtered unique index refuses two active rows either way.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status == ColdWalletStatus.Active)
            return;

        Status = ColdWalletStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>
    /// Stops this wallet receiving sweeps. The row stays, and its address stays in the custody sum: retiring
    /// a destination says nothing about whether it still holds funds.
    /// </summary>
    public void Retire(DateTimeOffset now)
    {
        if (Status == ColdWalletStatus.Retired)
            return;

        Status = ColdWalletStatus.Retired;
        UpdatedAt = now;
    }

    public void Rename(string? label, DateTimeOffset now)
    {
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        UpdatedAt = now;
    }

    /// <summary>Snapshots the latest screening verdict onto the row, so the staff list shows it without a
    /// provider call and without a join. The evidence itself stays append-only in Compliance.</summary>
    public void RecordScreening(
        Guid screeningId, string decision, int? score, DateTimeOffset screenedAt, DateTimeOffset now)
    {
        ScreeningId = screeningId;
        ScreeningDecision = decision;
        ScreeningScore = score;
        ScreenedAt = screenedAt;
        UpdatedAt = now;
    }
}
