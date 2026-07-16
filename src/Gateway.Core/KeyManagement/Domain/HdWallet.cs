using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

/// <summary>
/// One mnemonic, held outside this system, that generates a tree of addresses.
///
/// This entity holds <b>references</b> to key material, never key material. There is deliberately
/// no property — and no column — for a mnemonic, seed, or private key (§10). The
/// <see cref="PublicKeyReference"/> points at the account xpub, which is kept in the secret store
/// rather than the database: an xpub together with any single leaked non-hardened child private key
/// mathematically reveals the account's private key, and therefore every address under it.
/// </summary>
public sealed class HdWallet : Entity<Guid>
{
    private DerivationPath? _derivationPath;

    private HdWallet(
        Guid id,
        Guid? merchantId,
        string name,
        Chain chain,
        HdWalletPurpose purpose,
        DerivationScheme scheme,
        SecretProviderKind secretProvider,
        string secretReference,
        string? publicKeyReference,
        DerivationPath derivationPath,
        string? description,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        Name = name;
        Chain = chain;
        Purpose = purpose;
        Scheme = scheme;
        SecretProvider = secretProvider;
        SecretReference = secretReference;
        PublicKeyReference = publicKeyReference;
        DerivationPathValue = derivationPath.Value;
        _derivationPath = derivationPath;
        NextDerivationIndex = 0;
        Status = HdWalletStatus.Active;
        Description = description;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private HdWallet() : base(Guid.Empty)
    {
    }

    /// <summary>
    /// The merchant this wallet belongs to, or <c>null</c> for a platform wallet (treasury, hot, energy).
    /// Each merchant gets their own HD wallet — a distinct seed — so one merchant's key compromise cannot
    /// expose another's addresses (§10). Deposit addresses derive only from the owning merchant's tree.
    /// </summary>
    public Guid? MerchantId { get; private set; }

    public string Name { get; private set; } = null!;
    public Chain Chain { get; private set; }
    public HdWalletPurpose Purpose { get; private set; }
    public DerivationScheme Scheme { get; private set; }
    public SecretProviderKind SecretProvider { get; private set; }

    /// <summary>An ARN / vault path. A reference, never the secret.</summary>
    public string SecretReference { get; private set; } = null!;

    /// <summary>Reference to the account xpub in the secret store. Null for ed25519 wallets.</summary>
    public string? PublicKeyReference { get; private set; }

    /// <summary>The persisted form, e.g. <c>m/44'/195'/0'/0</c>.</summary>
    public string DerivationPathValue { get; private set; } = null!;

    /// <summary>
    /// Rebuilt from <see cref="DerivationPathValue"/> plus <see cref="Chain"/> and <see cref="Scheme"/>
    /// — a value converter cannot do this alone, because the string does not carry the chain.
    /// </summary>
    public DerivationPath DerivationPath =>
        _derivationPath ??= Domain.DerivationPath.FromTrusted(DerivationPathValue, Chain, Scheme);

    /// <summary>The next index to hand out. Allocated atomically by the database, never here.</summary>
    public long NextDerivationIndex { get; private set; }

    public HdWalletStatus Status { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == HdWalletStatus.Active;

    /// <summary>True when addresses derive from the xpub alone, with no access to the seed.</summary>
    public bool SupportsWatchOnlyDerivation => Scheme == DerivationScheme.Bip32Secp256k1;

    public bool IsExhausted => NextDerivationIndex > DerivationPath.MaxIndex;

    public static Result<HdWallet> Create(
        string name,
        Chain chain,
        HdWalletPurpose purpose,
        SecretProviderKind secretProvider,
        string secretReference,
        string? publicKeyReference,
        string derivationPath,
        string? description = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<HdWallet>(KeyManagementErrors.NameRequired);

        if (string.IsNullOrWhiteSpace(secretReference))
            return Result.Failure<HdWallet>(KeyManagementErrors.SecretReferenceRequired);

        var pathResult = Domain.DerivationPath.Create(derivationPath, chain);
        if (pathResult.IsFailure)
            return Result.Failure<HdWallet>(pathResult.Error!);

        var path = pathResult.Value;

        // A secp256k1 wallet without an xpub reference would be forced to fetch the seed to derive
        // an address — exactly what the watch-only design exists to avoid.
        if (path.Scheme == DerivationScheme.Bip32Secp256k1 && string.IsNullOrWhiteSpace(publicKeyReference))
            return Result.Failure<HdWallet>(KeyManagementErrors.PublicKeyReferenceRequired);

        // An ed25519 wallet cannot derive from a public key at all; carrying one implies otherwise.
        if (path.Scheme == DerivationScheme.Slip10Ed25519 && !string.IsNullOrWhiteSpace(publicKeyReference))
            return Result.Failure<HdWallet>(KeyManagementErrors.PublicKeyReferenceNotApplicable);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        return Result.Success(new HdWallet(
            Guid.CreateVersion7(), merchantId: null, name.Trim(), chain, purpose, path.Scheme,
            secretProvider, secretReference.Trim(),
            string.IsNullOrWhiteSpace(publicKeyReference) ? null : publicKeyReference.Trim(),
            path, description, now));
    }

    /// <summary>
    /// A merchant-owned HD wallet: its own seed, so deposit addresses for this merchant derive from a tree
    /// no other merchant shares. Always <see cref="HdWalletPurpose.Deposit"/> and watch-only (secp256k1) —
    /// the account xpub is exported once at provisioning and every address derives from it with no seed
    /// access (§8, §10). Created lazily on the merchant's first deposit.
    /// </summary>
    public static Result<HdWallet> CreateMerchantDeposit(
        Guid merchantId,
        string name,
        Chain chain,
        SecretProviderKind secretProvider,
        string secretReference,
        string publicKeyReference,
        string derivationPath,
        string? description = null,
        TimeProvider? timeProvider = null)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<HdWallet>(KeyManagementErrors.MerchantRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<HdWallet>(KeyManagementErrors.NameRequired);

        if (string.IsNullOrWhiteSpace(secretReference))
            return Result.Failure<HdWallet>(KeyManagementErrors.SecretReferenceRequired);

        var pathResult = Domain.DerivationPath.Create(derivationPath, chain);
        if (pathResult.IsFailure)
            return Result.Failure<HdWallet>(pathResult.Error!);

        var path = pathResult.Value;

        // Merchant deposit wallets are watch-only by construction: ed25519 (Solana) cannot derive addresses
        // from an xpub, so per-merchant Solana deposit wallets are deliberately not built yet (§8).
        if (path.Scheme != DerivationScheme.Bip32Secp256k1)
            return Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported);

        if (string.IsNullOrWhiteSpace(publicKeyReference))
            return Result.Failure<HdWallet>(KeyManagementErrors.PublicKeyReferenceRequired);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        return Result.Success(new HdWallet(
            Guid.CreateVersion7(), merchantId, name.Trim(), chain, HdWalletPurpose.Deposit, path.Scheme,
            secretProvider, secretReference.Trim(), publicKeyReference.Trim(), path, description, now));
    }

    public void Archive(DateTimeOffset now)
    {
        Status = HdWalletStatus.Archived;
        UpdatedAt = now;
    }

    public void Disable(DateTimeOffset now)
    {
        Status = HdWalletStatus.Disabled;
        UpdatedAt = now;
    }

    /// <summary>
    /// Mints the record for the key at <paramref name="index"/>. The aggregate owns this so the
    /// index bound and the recorded path are always derived from <em>this</em> wallet's path, never
    /// assembled by a caller.
    /// </summary>
    public Result<DerivedKey> DeriveKey(long index, string address, DateTimeOffset now)
    {
        if (!IsActive)
            return Result.Failure<DerivedKey>(KeyManagementErrors.NotActive);

        var pathResult = DerivationPath.AddressPathFor(index);
        if (pathResult.IsFailure)
            return Result.Failure<DerivedKey>(pathResult.Error!);

        return DerivedKey.Create(Id, index, Chain, address, pathResult.Value, now);
    }

    /// <summary>
    /// Test/reconstruction seam only. Production allocation is a single atomic
    /// <c>UPDATE … OUTPUT deleted.NextDerivationIndex</c>, because a read-modify-write here would
    /// let two concurrent callers hand out the same index — and a reused index means two merchants
    /// share one address.
    /// </summary>
    internal void SetNextDerivationIndex(long next) => NextDerivationIndex = next;
}
