using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;

/// <summary>A freshly provisioned dedicated deposit address: the opaque wallet id plus its public address.</summary>
public sealed record ProvisionedDepositAddress(Guid WalletId, Chain Chain, string Address);

/// <summary>
/// The Wallet module's public provisioning capability: derive a new dedicated deposit address for a
/// merchant on a chain (HD-derived, KMS-custodied — the caller never sees a key). Exposed via Contracts
/// so another module (e.g. PaymentIntent) can grow a merchant's address pool without touching Wallet
/// internals (§4.5). Reuse/rotation of existing addresses is the caller's concern; this only ever mints new.
/// </summary>
public interface IDepositAddressProvisioner
{
    Task<Result<ProvisionedDepositAddress>> ProvisionDepositAddressAsync(
        Guid merchantId,
        Chain chain,
        CancellationToken cancellationToken = default);
}
