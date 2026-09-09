using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;

/// <summary>
/// The platform-wide per-chain deposit "dust floor" — the same <c>Deposit:Policies:{Chain}:MinDepositBaseUnits</c>
/// config value the Deposit module reads for its own detection dust filter. PaymentIntent reads its own copy
/// of this raw config value (rather than depending on Deposit's <c>IDepositPolicyProvider</c>, which lives in
/// Deposit's Application/Infrastructure — off-limits across a module boundary, §4.5) so an invoice with no
/// merchant-specific minimum still has a sane floor to fall back to instead of accepting a zero/dust invoice.
/// </summary>
public interface IPlatformDepositFloor
{
    BigInteger For(Chain chain);
}
