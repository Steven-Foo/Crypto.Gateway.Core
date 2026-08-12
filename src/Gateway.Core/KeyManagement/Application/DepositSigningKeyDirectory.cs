using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;

/// <summary>
/// Resolves a merchant deposit address to its signing-key reference (for Sweep). It reads the derived key +
/// its owning HD wallet in one query and composes the reference the signer will resolve — it never exposes,
/// returns, or logs key material (§10). Returns null when no active deposit key matches the address.
/// </summary>
public sealed class DepositSigningKeyDirectory(IHdWalletRepository repository) : IDepositSigningKeyDirectory
{
    public async Task<DepositSigningKey?> FindByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var info = await repository.FindDepositSigningKeyByAddressAsync(chain, address, cancellationToken);
        if (info is null)
            return null;

        // KeyManagement's signer-resolution format for an HD deposit key: seed reference + child index.
        // Opaque to callers — the (future) envelope/KMS signer parses it; the in-memory dev signer ignores it
        // entirely (it never touches a key). A platform imported key, by contrast, has no index and its
        // reference is the bare secret reference — the signer distinguishes the two forms (§10).
        var keyReference = $"{info.SecretReference}#{info.DerivationIndex}";
        return new DepositSigningKey(info.DerivedKeyId, info.Chain, keyReference);
    }
}
