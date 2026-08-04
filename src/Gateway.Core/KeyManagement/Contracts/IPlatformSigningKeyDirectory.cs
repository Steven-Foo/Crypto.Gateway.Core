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
}
