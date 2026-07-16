using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;

/// <summary>
/// A platform (non-merchant) wallet — hot-withdrawal, treasury, energy, or cold. <see cref="WalletType"/>
/// is a string, matching <see cref="WalletOwnership"/>: consumers never depend on the Wallet module's enum (§4.5).
/// </summary>
public sealed record PlatformWallet(Guid WalletId, Chain Chain, string Address, string WalletType);

/// <summary>
/// The Wallet module's read model for the platform wallets other modules operate on. The Energy monitor
/// uses this to learn which hot/treasury/energy wallets to watch, without touching Wallet internals (§4.5).
/// Merchant deposit addresses are excluded by design (their energy is a sweep-time concern, Phase 5b).
/// </summary>
public interface IPlatformWalletDirectory
{
    Task<IReadOnlyList<PlatformWallet>> GetPlatformWalletsAsync(
        Chain chain, CancellationToken cancellationToken = default);
}
