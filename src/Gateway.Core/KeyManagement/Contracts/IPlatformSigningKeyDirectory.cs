using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;

/// <summary>
/// The signing-key reference for a platform (non-merchant) HD wallet — a reference, never the key
/// material (§10). A consumer quotes <see cref="KeyReference"/> straight to <see cref="ISigner"/>; it can
/// never resolve it to raw bytes itself.
/// </summary>
public sealed record PlatformSigningKey(Guid HdWalletId, Chain Chain, string KeyReference);

/// <summary>
/// The read counterpart to <see cref="IWalletDerivation"/> for platform wallets: resolves <em>what to sign
/// with</em> for a platform wallet of a given chain and purpose, without exposing HD-wallet internals or
/// secret material. Returns <c>null</c> when no active platform wallet is registered for that pair —
/// signing then stays inert rather than falling back to anything (§10).
/// </summary>
public interface IPlatformSigningKeyDirectory
{
    Task<PlatformSigningKey?> FindActiveAsync(
        Chain chain, DerivationPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the signing-key reference for a specific platform <em>withdrawal</em> address — a child of the
    /// platform withdrawal HD wallet (the hot pool). Unlike <see cref="FindActiveAsync"/> (one key per
    /// chain+purpose), the pool has many child addresses under one seed, so its key is resolved <em>per
    /// address</em> — the reference conveys the seed reference and the child index (the signer parses it, no
    /// caller does, §10). Returns null when no active withdrawal child matches the address.
    /// </summary>
    Task<PlatformSigningKey?> FindByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);
}
