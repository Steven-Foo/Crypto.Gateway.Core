using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;

/// <summary>
/// What custody hands back to the Wallet module. <see cref="DerivedKeyId"/> is an opaque handle:
/// the Wallet module stores it and quotes it back when a signature is needed, but can never learn
/// which HD wallet or index produced it, and certainly never a key.
/// </summary>
public sealed record DerivedAddress(
    Guid DerivedKeyId,
    Chain Chain,
    string Address,
    long DerivationIndex,
    string DerivationPath);

public enum DerivationPurpose
{
    Deposit = 1,
    Withdrawal = 2,
    Treasury = 3,
    Energy = 4,
    Cold = 5,
}

public interface IWalletDerivation
{
    /// <summary>
    /// Allocates the next index for the single active <em>platform</em> HD wallet of this chain and purpose,
    /// derives its address, and records the key — all in one transaction. Safe under concurrency: callers
    /// never receive the same index twice.
    /// </summary>
    Task<Result<DerivedAddress>> AllocateNextAsync(
        Chain chain,
        DerivationPurpose purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates the next address from <paramref name="merchantId"/>'s own HD wallet, creating that wallet
    /// (its own seed) on the merchant's first deposit. Each merchant's addresses derive from a tree no other
    /// merchant shares (§10), and each has an independent, atomically-allocated index sequence. Concurrency-
    /// safe on both the create-on-first-use and the index allocation.
    /// </summary>
    Task<Result<DerivedAddress>> AllocateNextForMerchantAsync(
        Guid merchantId,
        Chain chain,
        DerivationPurpose purpose,
        CancellationToken cancellationToken = default);

    Task<DerivedAddress?> FindAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);
}
