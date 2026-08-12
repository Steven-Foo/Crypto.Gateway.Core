using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;

/// <summary>
/// Resolves the signing-key reference for the single active platform HD wallet of a chain/purpose, over
/// the existing <see cref="IHdWalletRepository.FindActiveAsync"/> read — no new query. Returns only a
/// reference (<see cref="HdWallet.SecretReference"/>), never key material (§10).
/// </summary>
public sealed class PlatformSigningKeyDirectory(IHdWalletRepository repository) : IPlatformSigningKeyDirectory
{
    public async Task<PlatformSigningKey?> FindActiveAsync(
        Chain chain, DerivationPurpose purpose, CancellationToken cancellationToken = default)
    {
        var hdWallet = await repository.FindActiveAsync(chain, (HdWalletPurpose)purpose, cancellationToken);

        return hdWallet is null
            ? null
            : new PlatformSigningKey(hdWallet.Id, hdWallet.Chain, hdWallet.SecretReference);
    }

    public async Task<PlatformSigningKey?> FindByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var info = await repository.FindPlatformWithdrawalSigningKeyByAddressAsync(chain, address, cancellationToken);
        if (info is null)
            return null;

        // Same signer-resolution grammar as an HD deposit key: seed reference + child index. Opaque to
        // callers — the (future) envelope/KMS signer parses it; the in-memory dev signer ignores it (§10).
        var keyReference = $"{info.SecretReference}#{info.DerivationIndex}";
        return new PlatformSigningKey(info.DerivedKeyId, info.Chain, keyReference);
    }
}
