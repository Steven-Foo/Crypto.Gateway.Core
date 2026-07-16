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
    TimeProvider timeProvider,
    IEnumerable<IHdWalletProvisioner> provisioners) : IWalletDerivation
{
    // Optional dependency, the DI-friendly way: empty in production (no provisioner registered → per-merchant
    // minting is deferred, never a silent in-memory fallback, §10); the dev provisioner when registered.
    private readonly IHdWalletProvisioner? _provisioner = provisioners.FirstOrDefault();

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

        return await AllocateFromAsync(hdWallet, chain, cancellationToken);
    }

    public async Task<Result<DerivedAddress>> AllocateNextForMerchantAsync(
        Guid merchantId,
        Chain chain,
        DerivationPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<DerivedAddress>(KeyManagementErrors.MerchantRequired);

        var hdWallet = await repository.FindActiveForMerchantAsync(merchantId, chain, (HdWalletPurpose)purpose, cancellationToken);
        if (hdWallet is null)
        {
            var provisioned = await ProvisionMerchantWalletAsync(merchantId, chain, purpose, cancellationToken);
            if (provisioned.IsFailure)
                return Result.Failure<DerivedAddress>(provisioned.Error!);

            hdWallet = provisioned.Value;
        }

        return await AllocateFromAsync(hdWallet, chain, cancellationToken);
    }

    public async Task<DerivedAddress?> FindAsync(Guid derivedKeyId, CancellationToken cancellationToken = default)
    {
        var key = await repository.FindDerivedKeyAsync(derivedKeyId, cancellationToken);

        return key is null
            ? null
            : new DerivedAddress(key.Id, key.Chain, key.Address, key.DerivationIndex, key.DerivationPath);
    }

    /// <summary>
    /// Creates a merchant's HD wallet on first use. The unique <c>(MerchantId, Chain, Purpose)</c> index is
    /// the race arbiter: if a concurrent first deposit won, our insert fails and we adopt the winner — so two
    /// requests never mint two seeds for one merchant. Only deposits are per-merchant, and only when a
    /// provisioner is registered (dev in-memory; production KMS-backed, deferred).
    /// </summary>
    private async Task<Result<HdWallet>> ProvisionMerchantWalletAsync(
        Guid merchantId, Chain chain, DerivationPurpose purpose, CancellationToken cancellationToken)
    {
        if (purpose != DerivationPurpose.Deposit)
            return Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported);

        // No provisioner (production, until a KMS-backed one lands) → a merchant wallet cannot be minted, so
        // deposit provisioning stays inert rather than silently falling back to an in-memory seed (§10).
        if (_provisioner is null)
            return Result.Failure<HdWallet>(KeyManagementErrors.NotFound);

        var walletResult = await _provisioner.ProvisionMerchantDepositWalletAsync(merchantId, chain, cancellationToken);
        if (walletResult.IsFailure)
            return walletResult;

        var wallet = walletResult.Value;
        var outcome = await repository.TryAddActiveAsync(wallet, cancellationToken);
        if (outcome == HdWalletAddOutcome.Added)
            return Result.Success(wallet);

        // Lost the create-on-first-use race — adopt the wallet the winner committed.
        var winner = await repository.FindActiveForMerchantAsync(merchantId, chain, (HdWalletPurpose)purpose, cancellationToken);
        return winner is not null
            ? Result.Success(winner)
            : Result.Failure<HdWallet>(KeyManagementErrors.NotFound);
    }

    /// <summary>
    /// Allocates one index from an already-resolved HD wallet and derives its address, watch-only. Shared by
    /// the platform and per-merchant paths — the only difference between them is which wallet is resolved.
    /// </summary>
    private async Task<Result<DerivedAddress>> AllocateFromAsync(
        HdWallet hdWallet, Chain chain, CancellationToken cancellationToken)
    {
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
