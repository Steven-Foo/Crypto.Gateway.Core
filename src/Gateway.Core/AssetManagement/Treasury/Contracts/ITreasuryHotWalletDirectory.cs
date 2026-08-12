using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;

/// <summary>One platform hot withdrawal wallet: its <see cref="WalletId"/> (the Wallet-module row id — the
/// withdrawal module records it as the payout's source), its address, plus the reference the signer quotes to
/// sign with. <see cref="KeyReference"/> is a reference, never key material (§10).</summary>
public sealed record TreasuryHotWallet(Guid WalletId, Chain Chain, string Address, string KeyReference);

/// <summary>
/// The read seam the Withdrawal module consumes to learn which hot wallets to sign from. Combines the Wallet
/// module's registered <c>HotWithdrawal</c> addresses with each one's per-address KeyManagement signing-key
/// reference, each read through its own Contracts (§4.5). The hot wallets are a <b>pool</b> — watch-only
/// children of one platform withdrawal HD wallet — so <see cref="GetHotWalletPoolAsync"/> returns all of them;
/// <see cref="GetHotWalletAsync"/> returns a single one (the first) for callers that still need one
/// destination (Sweep, until it targets the cold treasury in a later phase).
/// </summary>
public interface ITreasuryHotWalletDirectory
{
    /// <summary>A single hot wallet (the first registered) — for Sweep's single-destination use. Fails if none.</summary>
    Task<Result<TreasuryHotWallet>> GetHotWalletAsync(Chain chain, CancellationToken cancellationToken = default);

    /// <summary>Every registered hot withdrawal wallet for the chain (the pool). Empty list when none are
    /// registered — never an error, so an allocator can distinguish "no pool" from a fault.</summary>
    Task<IReadOnlyList<TreasuryHotWallet>> GetHotWalletPoolAsync(Chain chain, CancellationToken cancellationToken = default);
}
