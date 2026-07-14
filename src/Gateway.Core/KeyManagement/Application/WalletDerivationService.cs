using System.Collections.Concurrent;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;

public sealed class WalletDerivationService(
    IHdWalletRepository repository,
    IKeyDeriverFactory keyDeriverFactory,
    IAddressEncoderFactory addressEncoderFactory,
    ISecretProviderFactory secretProviderFactory,
    TimeProvider timeProvider) : IWalletDerivation
{
    /// <summary>
    /// Account xpubs are public data and immutable for the life of the HD wallet, so caching them
    /// avoids a secret-store round trip while the index row is locked. Caching a <em>seed</em> here
    /// would be indefensible; caching a public key is not.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _accountPublicKeys = new();

    public async Task<Result<DerivedAddress>> AllocateNextAsync(
        Chain chain,
        DerivationPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var hdWallet = await repository.FindActiveAsync(chain, (HdWalletPurpose)purpose, cancellationToken);
        if (hdWallet is null)
            return Result.Failure<DerivedAddress>(KeyManagementErrors.NotFound);

        if (!addressEncoderFactory.Supports(chain))
            return Result.Failure<DerivedAddress>(KeyManagementErrors.ChainNotSupported);

        if (!keyDeriverFactory.SupportsWatchOnly(hdWallet.Scheme))
        {
            // ed25519 wallets need seed access to derive an address. That path is deliberately not
            // implemented yet rather than half-implemented on the custody boundary.
            return Result.Failure<DerivedAddress>(KeyManagementErrors.SchemeNotSupported);
        }

        // Fetched before the transaction opens, so the index row lock is held only for DB work.
        var accountPublicKeyResult = await GetAccountPublicKeyAsync(hdWallet, cancellationToken);
        if (accountPublicKeyResult.IsFailure)
            return Result.Failure<DerivedAddress>(accountPublicKeyResult.Error!);

        var accountPublicKey = accountPublicKeyResult.Value;

        return await repository.InTransactionAsync(async ct =>
        {
            var indexResult = await repository.AllocateNextIndexAsync(hdWallet.Id, ct);
            if (indexResult.IsFailure)
                return Result.Failure<DerivedAddress>(indexResult.Error!);

            var index = indexResult.Value;

            var publicKey = keyDeriverFactory.WatchOnlyFor(hdWallet.Scheme).DerivePublicKey(accountPublicKey, index);
            var address = addressEncoderFactory.For(chain).Encode(publicKey);

            var derivedKeyResult = hdWallet.DeriveKey(index, address, timeProvider.GetUtcNow());
            if (derivedKeyResult.IsFailure)
                return Result.Failure<DerivedAddress>(derivedKeyResult.Error!);

            var derivedKey = derivedKeyResult.Value;
            repository.AddDerivedKey(derivedKey);
            await repository.SaveChangesAsync(ct);

            return Result.Success(new DerivedAddress(
                derivedKey.Id, chain, derivedKey.Address, derivedKey.DerivationIndex, derivedKey.DerivationPath));
        }, cancellationToken);
    }

    public async Task<DerivedAddress?> FindAsync(Guid derivedKeyId, CancellationToken cancellationToken = default)
    {
        var key = await repository.FindDerivedKeyAsync(derivedKeyId, cancellationToken);

        return key is null
            ? null
            : new DerivedAddress(key.Id, key.Chain, key.Address, key.DerivationIndex, key.DerivationPath);
    }

    private async Task<Result<string>> GetAccountPublicKeyAsync(HdWallet hdWallet, CancellationToken cancellationToken)
    {
        if (_accountPublicKeys.TryGetValue(hdWallet.Id, out var cached))
            return Result.Success(cached);

        if (hdWallet.PublicKeyReference is not { } reference)
            return Result.Failure<string>(KeyManagementErrors.PublicKeyReferenceRequired);

        if (!secretProviderFactory.Supports(hdWallet.SecretProvider))
            return Result.Failure<string>(KeyManagementErrors.SchemeNotSupported);

        using var lease = await secretProviderFactory.For(hdWallet.SecretProvider).GetAsync(reference, cancellationToken);

        // An xpub is public material — safe to hold as a string. A seed would not be.
        var accountPublicKey = lease.AsPublicUtf8String();
        _accountPublicKeys[hdWallet.Id] = accountPublicKey;

        return Result.Success(accountPublicKey);
    }
}
