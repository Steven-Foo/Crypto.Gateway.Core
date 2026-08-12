using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON read-only <see cref="IBalanceReader"/>. Resolves an <c>AssetId</c> to its token contract via the
/// asset catalog and reads the on-chain balance: a TRC-20 token (e.g. USDT) via <c>eth_call balanceOf</c>,
/// or native TRX via <c>eth_getBalance</c>. Amounts are exact base units (sun for TRX, the token's own base
/// units for TRC-20) — never a scaled display value (§14). Read-only: no key ever crosses it (§10).
/// </summary>
public sealed class TronBalanceReader(ITronRpc rpc, IAssetCatalog assetCatalog) : IBalanceReader
{
    public async Task<BigInteger> GetBalanceAsync(
        Chain chain, string address, Guid assetId, CancellationToken cancellationToken = default)
    {
        if (chain != Chain.Tron)
            throw new ArgumentException($"TronBalanceReader cannot read a {chain} balance.", nameof(chain));

        var asset = await assetCatalog.FindByIdAsync(assetId, cancellationToken)
            ?? throw new InvalidOperationException($"Asset {assetId} is not in the catalog; cannot read its on-chain balance.");

        if (asset.Chain != Chain.Tron)
            throw new InvalidOperationException($"Asset {assetId} is a {asset.Chain} asset, not TRON.");

        if (asset.IsNative)
            return await rpc.GetNativeBalanceAsync(TronAddress.ToEvmHex(address), cancellationToken);

        if (string.IsNullOrWhiteSpace(asset.ContractAddress))
            throw new InvalidOperationException($"TRC-20 asset {assetId} ({asset.Symbol}) has no contract address configured.");

        var contractHex = TronAddress.ToEvmHex(asset.ContractAddress);
        var data = TronAbi.EncodeBalanceOf(address);

        var result = await rpc.CallContractAsync(contractHex, data, cancellationToken);

        // A non-contract address, an unfunded holder, or "0x" all decode to zero — never an error.
        return HexNumber.ToBigInteger(result);
    }
}
