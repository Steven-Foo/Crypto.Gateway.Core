using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;

/// <summary>Whether an address is ready to send a TRON transfer without burning TRX for energy.</summary>
public enum EnergyReadiness
{
    /// <summary>The address already has enough energy — proceed.</summary>
    Ready = 1,

    /// <summary>Energy is being provisioned (a delegation is in flight or was just created) — retry later.</summary>
    Provisioning = 2,

    /// <summary>Energy could not be provisioned (no staking wallet registered, or auto-delegate disabled) —
    /// the caller decides what to do (Sweep keeps the transfer waiting rather than burning TRX).</summary>
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
