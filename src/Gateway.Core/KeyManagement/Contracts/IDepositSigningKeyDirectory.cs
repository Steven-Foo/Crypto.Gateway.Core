using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;

/// <summary>
/// The signing-key reference for a merchant <em>deposit</em> address — a reference, never key material (§10).
/// Unlike a platform wallet (a single imported key), a deposit address is an HD-derived child, so its
/// <see cref="KeyReference"/> must convey both the seed and the child index; the exact grammar is
/// KeyManagement-internal (the signer resolves it, no caller ever does). A consumer quotes it straight to
/// <see cref="ISigner"/>.
/// </summary>
public sealed record DepositSigningKey(Guid DerivedKeyId, Chain Chain, string KeyReference);

/// <summary>
/// Resolves <em>what to sign with</em> for a merchant deposit address — used by Sweep to sign a transfer
/// FROM a deposit address into the hot wallet, without exposing HD-wallet internals or secret material.
/// Returns <c>null</c> when no active deposit key matches the address; sweeping that address then stays
/// inert rather than falling back to anything (§10).
/// </summary>
public interface IDepositSigningKeyDirectory
{
    Task<DepositSigningKey?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}
