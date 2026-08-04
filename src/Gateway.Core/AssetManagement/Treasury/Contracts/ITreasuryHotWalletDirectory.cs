using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;

/// <summary>The platform's hot withdrawal wallet for a chain: its address plus the reference the signer
/// quotes to sign with. <see cref="KeyReference"/> is a reference, never key material (§10).</summary>
public sealed record TreasuryHotWallet(Chain Chain, string Address, string KeyReference);

/// <summary>
/// The read seam the Withdrawal module consumes to learn which hot wallet to sign from — replacing the old
/// raw-config lookup. Combines the Wallet module's registered <c>HotWithdrawal</c> address with the
/// KeyManagement module's signing-key reference, each read through its own Contracts (§4.5). Fails (never
/// silently picks one) if no single hot wallet is registered for the chain.
/// </summary>
public interface ITreasuryHotWalletDirectory
{
    Task<Result<TreasuryHotWallet>> GetHotWalletAsync(Chain chain, CancellationToken cancellationToken = default);
}
