using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>
/// Reads an address's balance of the chain's OWN coin — the gas coin (TRX on TRON), as opposed to a token like
/// USDT. Its own small port (§8 capability segregation) rather than a native <c>AssetId</c> through
/// <see cref="IBalanceReader"/>: the gas coin is deliberately kept OUT of the asset catalog, because a catalog
/// entry would switch on deposit scanning for it. Read-only and keyless (§10).
/// </summary>
public interface INativeBalanceReader
{
    /// <summary>The native-coin balance in its base unit (sun for TRX).</summary>
    Task<BigInteger> GetNativeBalanceAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}

/// <summary>A chain's native gas coin: what to call it and how many decimals its base unit has.</summary>
public sealed record NativeCoin(string Symbol, int Decimals);

/// <summary>
/// The native gas coin of each supported chain — reference data, so a screen never hard-codes "TRX, 6 dp" and a
/// new chain needs no frontend change.
/// </summary>
public static class NativeCoins
{
    public static NativeCoin For(Chain chain) => chain switch
    {
        Chain.Tron => new NativeCoin("TRX", 6),        // 1 TRX = 1,000,000 sun
        Chain.Ethereum => new NativeCoin("ETH", 18),   // 1 ETH = 10^18 wei
        Chain.Solana => new NativeCoin("SOL", 9),      // 1 SOL = 10^9 lamports
        _ => throw new ArgumentOutOfRangeException(nameof(chain), chain, "No native coin defined for this chain."),
    };
}
