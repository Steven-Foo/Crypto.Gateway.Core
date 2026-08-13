using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;

/// <summary>Whether an address is ready to send a TRON transfer without burning TRX — checking both
/// energy (TRC-20 execution) and bandwidth (transaction size).</summary>
public enum EnergyReadiness
{
    /// <summary>The address has enough energy AND can pay bandwidth (free allotment or a TRX cushion) — proceed.</summary>
    Ready = 1,

    /// <summary>Energy is being provisioned (a delegation is in flight or was just created) — retry later.</summary>
    Provisioning = 2,

    /// <summary>The address cannot be readied: energy couldn't be delegated (no staking wallet registered), or
    /// it has energy but neither free bandwidth nor a spendable-TRX cushion to cover the bandwidth burn. The
    /// caller keeps the transfer waiting (funds are safe) — a TRX top-up is needed.</summary>
    Unavailable = 3,
}

/// <summary>
/// The Energy module's public seam for other modules to get an address ready to transact on TRON (§4.5). It
/// ensures a target address has enough energy, delegating from the platform staking wallet if short — used by
/// Sweep to prepare a deposit address before sweeping it, so the sweep doesn't burn TRX. It never moves the
/// caller's money and posts no ledger entry (delegated energy is recoverable, §15.4).
///
/// It is <em>not</em> blocking: it reads the current energy and, if short, ensures a delegation exists
/// (creating a Pending one if none is in flight) and returns <see cref="EnergyReadiness.Provisioning"/> — the
/// delegation confirms asynchronously via Energy's own workers, and the caller retries.
/// </summary>
public interface IEnergyDelegationService
{
    Task<EnergyReadiness> EnsureEnergyForTransferAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}
