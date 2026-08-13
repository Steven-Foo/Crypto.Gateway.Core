using System.Text.Json;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON resource (energy/bandwidth) operations over the native <c>/wallet/*</c> HTTP API — segregated from
/// <see cref="ITronRpc"/> and <see cref="ITronTxRpc"/> so adding it doesn't perturb the scanner's or money-out
/// adapters' fakes. <b>Keyless (§10):</b> the two reads only observe standing; <c>freezebalancev2</c> and
/// <c>delegateresource</c> return an <em>UNSIGNED</em> transaction (like <c>triggersmartcontract</c>) — a
/// caller still cannot lock or move funds without the separate <c>ISigner</c>. Implemented by <c>TronRpc</c>.
/// </summary>
public interface ITronResourceRpc
{
    /// <summary><c>/wallet/getaccountresource</c> — energy + bandwidth limits/usage for an address.</summary>
    Task<TronAccountResourceDto> GetAccountResourceAsync(string ownerHexAddress, CancellationToken cancellationToken = default);

    /// <summary><c>/wallet/getaccount</c> — the account's TRX balance (sun) and frozen positions.</summary>
    Task<TronAccountDto> GetAccountAsync(string ownerHexAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>/wallet/freezebalancev2</c> — builds an UNSIGNED stake-for-energy transaction. Returns the raw
    /// transaction JSON (txID / raw_data / raw_data_hex), or a <c>{ "Error": … }</c> object on rejection.
    /// </summary>
    Task<JsonElement> FreezeBalanceV2Async(FreezeBalanceV2Request request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>/wallet/delegateresource</c> — builds an UNSIGNED delegate-energy transaction. Returns the raw
    /// transaction JSON, or a <c>{ "Error": … }</c> object on rejection.
    /// </summary>
    Task<JsonElement> DelegateResourceAsync(DelegateResourceRequest request, CancellationToken cancellationToken = default);
}
