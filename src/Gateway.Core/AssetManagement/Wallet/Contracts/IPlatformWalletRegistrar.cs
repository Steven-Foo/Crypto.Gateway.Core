using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;

/// <summary>What the Wallet module hands back once a platform wallet row exists.</summary>
public sealed record RegisteredPlatformWallet(Guid WalletId, Chain Chain, string Address, string WalletType);

/// <summary>
/// Registers a platform (non-merchant) wallet row for an already-derived/imported key. <paramref name="walletType"/>
/// is a string, matching <see cref="PlatformWallet"/>: callers never depend on the Wallet module's enum (§4.5).
/// Idempotent per <c>(chain, address)</c> — a second call for the same address returns the existing row.
/// The write counterpart to <see cref="IPlatformWalletDirectory"/>.
/// </summary>
public interface IPlatformWalletRegistrar
{
    Task<Result<RegisteredPlatformWallet>> RegisterPlatformWalletAsync(
        Guid derivedKeyId,
        Chain chain,
        string address,
        string walletType,
        string? description = null,
        CancellationToken cancellationToken = default);
}
