using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;

/// <summary>
/// Which class of funds a cold collection wallet collects.
///
/// <para><b>This is a routing class, not a verdict about the wallet.</b> Before a deposit address is swept
/// it is screened, and the answer decides which collection wallet receives the funds: clean balances go to
/// the <see cref="Safe"/> wallet, flagged balances to the <see cref="Danger"/> one, so tainted inflow is
/// never mixed into the treasury the platform treats as clean. Un-mixing is impossible after the fact —
/// once two balances share an address, everything that later leaves it carries the taint — which is why the
/// decision is made before the sweep rather than sorted out afterwards.</para>
/// </summary>
public enum ColdWalletKind
{
    /// <summary>Receives sweeps from deposit addresses that screened clean. The default destination, and the
    /// one every sweep uses when screening is switched off.</summary>
    Safe = 0,

    /// <summary>
    /// Receives sweeps from deposit addresses the screening provider flagged — a quarantine address.
    ///
    /// <para>Its own risk score is <b>expected</b> to degrade over time: it collects tainted inflow by
    /// design. Nothing in this system may therefore refuse to use it because it screens badly, or the
    /// control would disable itself precisely when it is doing its job.</para>
    /// </summary>
    Danger = 1,
}

/// <summary>Whether a registered cold collection wallet is the one currently receiving sweeps.</summary>
public enum ColdWalletStatus
{
    /// <summary>The destination for its (chain, kind). Exactly one wallet holds this at a time, enforced by
    /// a filtered unique index rather than by application logic.</summary>
    Active = 0,

    /// <summary>
    /// No longer receives sweeps, but is still a platform-controlled address.
    ///
    /// <para>Retired wallets are never deleted and stay in the custody total. A retired address usually
    /// still holds funds, and dropping it from the sum would read as an instant, unexplained shortfall in
    /// the reconciliation audit.</para>
    /// </summary>
    Retired = 1,
}

/// <summary>The cold collection wallet a sweep pays into — a watch-only address whose key the system never
/// holds (a human signs anything that leaves it, §10).</summary>
public sealed record ColdTreasuryWallet(Guid WalletId, Chain Chain, ColdWalletKind Kind, string Address);

/// <summary>
/// A registered cold collection wallet as staff see it, including the screening verdict last recorded for
/// its address.
/// </summary>
/// <param name="ScreeningDecision">Our decision under the policy in force when it was screened, or null if
/// it has never been screened. Informational here: a verdict never refuses a collection wallet — see
/// <see cref="ColdWalletKind.Danger"/>.</param>
public sealed record RegisteredColdTreasuryWallet(
    Guid WalletId,
    Chain Chain,
    ColdWalletKind Kind,
    string Address,
    string? Label,
    ColdWalletStatus Status,
    string? ScreeningDecision,
    int? ScreeningScore,
    Guid? ScreeningId,
    DateTimeOffset? ScreenedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The read seam other modules consume (§4.5): Sweep resolves its destination through
/// <see cref="GetAsync"/>, and Reconciliation includes every controlled cold address in the custody sum
/// through <see cref="ListCustodyAddressesAsync"/>.
/// </summary>
public interface ITreasuryColdWalletDirectory
{
    /// <summary>
    /// The active collection wallet for a chain and kind. Fails when none is registered, so a caller stays
    /// inert rather than moving funds to nowhere (§10) — in particular a chain with no Danger wallet must
    /// never fall back to the Safe one, which would defeat the segregation entirely.
    /// </summary>
    Task<Result<ColdTreasuryWallet>> GetAsync(
        Chain chain, ColdWalletKind kind, CancellationToken cancellationToken = default);

    /// <summary>Every registered collection wallet on every chain, retired ones included — the staff view of
    /// where swept funds are going and have gone.</summary>
    Task<IReadOnlyList<RegisteredColdTreasuryWallet>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every cold address the platform controls on a chain — <b>both kinds, active and retired</b>.
    ///
    /// <para>For the custody audit, which must sum what the platform holds, not what it is currently
    /// sweeping into. An address omitted here is drift that reconciliation invents out of nothing.</para>
    /// </summary>
    Task<IReadOnlyList<string>> ListCustodyAddressesAsync(
        Chain chain, CancellationToken cancellationToken = default);
}
