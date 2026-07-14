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
    /// Allocates the next index for the single active HD wallet of this chain and purpose, derives
    /// its address, and records the key — all in one transaction. Safe under concurrency: callers
    /// never receive the same index twice.
    /// </summary>
    Task<Result<DerivedAddress>> AllocateNextAsync(
        Chain chain,
        DerivationPurpose purpose,
        CancellationToken cancellationToken = default);

    Task<DerivedAddress?> FindAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);
}
