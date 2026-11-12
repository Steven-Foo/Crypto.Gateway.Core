using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Tunables for the 5b stake/delegate actions, all TRON base units (§14). Defaults are conservative
/// starting points; a host binds them from config. A per-asset energy estimate is a future refinement.
/// </summary>
public sealed class EnergyOperationOptions
{
    /// <summary>Energy an address needs before we delegate rather than let it burn TRX. A USDT (TRC-20)
    /// transfer costs roughly 65k–131k energy depending on the receiver's state; default is the safe upper end.</summary>
    public BigInteger RequiredEnergyPerTransfer { get; init; } = 131_000;

    /// <summary>Bandwidth a transfer needs. Unlike energy, we do NOT delegate bandwidth (its burn is trivial,
    /// ~0.27 TRX) — the gate only requires the address can cover it from free bandwidth or a small TRX cushion.
    /// A TRC-20 transfer is ~345 bytes; a native TRX transfer ~267. Default is the safe upper end.</summary>
    public BigInteger RequiredBandwidthPerTransfer { get; init; } = 400;

    /// <summary>The minimum spendable TRX (sun) an address must hold to cover a bandwidth burn when its free
    /// bandwidth is exhausted — the "leftover TRX funds the next sweep" cushion. Default 1 TRX, enough for
    /// several bandwidth burns. Below this AND out of free bandwidth ⇒ the address needs a TRX top-up.</summary>
    public BigInteger MinTrxCushionSun { get; init; } = 1_000_000; // 1 TRX

    /// <summary>
    /// FALLBACK TRX (sun) to lend a short address, used only when the network's energy rate cannot be read. The
    /// normal amount is calculated from that rate (see <see cref="DelegationSizing"/>): on mainnet a full
    /// transfer needs thousands of TRX lent, so a fixed small number does not work there.
    /// </summary>
    public BigInteger DelegateTrxSun { get; init; } = 20_000_000; // 20 TRX

    /// <summary>Extra lent on top of the calculated need, in percent, so a small rate change between reading
    /// it and the transfer does not leave the address a little short. Default 10.</summary>
    public int DelegateMarginPercent { get; init; } = 10;

    /// <summary>The most TRX (sun) one lend may use, whatever the calculation says — a guard against a wrong
    /// rate reading locking up the whole staking balance on one address. Default 50,000 TRX.</summary>
    public BigInteger MaxDelegateTrxSun { get; init; } = 50_000_000_000; // 50,000 TRX

    /// <summary>How long after a lend confirms before the take-back worker reclaims it. Long enough for the
    /// sweep it was lent for to happen; if that sweep is still waiting it simply borrows again. Default 30 min.</summary>
    public TimeSpan ReclaimAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The most addresses the take-back worker reclaims from in one pass. Default 50.</summary>
    public int ReclaimBatchSize { get; init; } = 50;

    /// <summary>TRX (sun) the gas hub sends to an address that has energy but can't pay bandwidth — a few
    /// bandwidth-burns' worth, left as its cushion. Default 2 TRX.</summary>
    public BigInteger TopUpTrxSun { get; init; } = 2_000_000; // 2 TRX

    /// <summary>TRX (sun) to freeze per auto-stake top-up of the staking wallet.</summary>
    public BigInteger StakeIncrementTrxSun { get; init; } = 100_000_000; // 100 TRX

    /// <summary>On-chain depth a stake/delegate transaction must reach to be treated as final.</summary>
    public int Confirmations { get; init; } = 19;
}
