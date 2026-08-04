using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;

/// <summary>
/// Registers a platform key whose material was imported directly (not HD-derived). Idempotent per
/// <c>(chain, purpose)</c>: a second call with the same address adopts the existing registration; a
/// different address is refused (<see cref="KeyManagementErrors.PlatformKeyAddressConflict"/>) rather
/// than silently reassigning a signing key.
///
/// Depends on a single <see cref="ISecretProvider"/> (constructor-injected, not the factory), mirroring
/// <c>TronSigner</c>: this service is only ever wired where exactly one provider is registered — the
/// dev/testnet tier — so the wallet's <see cref="HdWallet.SecretProvider"/> is stamped from it. That is
/// the seam that keeps a production implementation absent, not merely unused (§10).
/// </summary>
public sealed class PlatformKeyRegistrationService(
    IHdWalletRepository repository,
    ISecretProvider secretProvider,
    TimeProvider timeProvider) : IPlatformKeyRegistrar
{
    // Imported wallets carry exactly one recorded key at index 0 (there is no real derivation lineage).
    private const long ImportedKeyIndex = 0;

    public async Task<Result<RegisteredPlatformKey>> RegisterImportedKeyAsync(
        Chain chain,
        DerivationPurpose purpose,
        string address,
        string secretReference,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<RegisteredPlatformKey>(KeyManagementErrors.AddressRequired);

        if (string.IsNullOrWhiteSpace(secretReference))
            return Result.Failure<RegisteredPlatformKey>(KeyManagementErrors.SecretReferenceRequired);

        var trimmedAddress = address.Trim();
        var walletPurpose = (HdWalletPurpose)purpose;

        // Idempotent adopt: if a platform wallet is already registered for this (chain, purpose), it must
        // carry the same address — otherwise this is a conflict, never a silent reassignment. A non-null
        // result here (success OR conflict failure) is terminal; only null means "nothing yet, go create".
        var adopted = await TryAdoptExistingAsync(chain, walletPurpose, trimmedAddress, cancellationToken);
        if (adopted is not null)
            return adopted;

        var pathResult = Domain.DerivationPath.Create(DefaultAccountPath(chain), chain);
        if (pathResult.IsFailure)
            return Result.Failure<RegisteredPlatformKey>(pathResult.Error!);

        var walletResult = HdWallet.CreateImportedPlatformKey(
            NameFor(chain, walletPurpose), chain, walletPurpose, secretProvider.Kind,
            secretReference, pathResult.Value.Value, description, timeProvider);
        if (walletResult.IsFailure)
            return Result.Failure<RegisteredPlatformKey>(walletResult.Error!);

        var wallet = walletResult.Value;

        var derivedKeyResult = wallet.DeriveKey(ImportedKeyIndex, trimmedAddress, timeProvider.GetUtcNow());
        if (derivedKeyResult.IsFailure)
            return Result.Failure<RegisteredPlatformKey>(derivedKeyResult.Error!);

        var derivedKey = derivedKeyResult.Value;

        var outcome = await repository.TryAddImportedPlatformKeyAsync(wallet, derivedKey, cancellationToken);
        if (outcome == HdWalletAddOutcome.DuplicateActive)
        {
            // Lost the create race on the unique (MerchantId, Chain, Purpose) index — adopt the winner
            // (which must carry the same address, or it's a genuine conflict).
            var raced = await TryAdoptExistingAsync(chain, walletPurpose, trimmedAddress, cancellationToken);
            return raced ?? Result.Failure<RegisteredPlatformKey>(KeyManagementErrors.PlatformKeyAddressConflict);
        }

        return Result.Success(new RegisteredPlatformKey(
            derivedKey.Id, chain, derivedKey.Address, wallet.SecretReference));
    }

    private async Task<Result<RegisteredPlatformKey>?> TryAdoptExistingAsync(
        Chain chain, HdWalletPurpose purpose, string address, CancellationToken cancellationToken)
    {
        var existing = await repository.FindActiveAsync(chain, purpose, cancellationToken);
        if (existing is null)
            return null;

        var key = await repository.FindDerivedKeyForWalletAsync(existing.Id, ImportedKeyIndex, cancellationToken);
        if (key is null)
            return Result.Failure<RegisteredPlatformKey>(KeyManagementErrors.NotFound);

        return string.Equals(key.Address, address, StringComparison.Ordinal)
            ? Result.Success(new RegisteredPlatformKey(key.Id, chain, key.Address, existing.SecretReference))
            : Result.Failure<RegisteredPlatformKey>(KeyManagementErrors.PlatformKeyAddressConflict);
    }

    private static string NameFor(Chain chain, HdWalletPurpose purpose) => $"Platform {purpose} ({chain})";

    private static string DefaultAccountPath(Chain chain)
    {
        var coin = Domain.DerivationPath.CoinTypeFor(chain);
        return Domain.DerivationPath.SchemeFor(chain) == DerivationScheme.Bip32Secp256k1
            ? $"m/44'/{coin}'/0'/0"
            : $"m/44'/{coin}'";
    }
}
