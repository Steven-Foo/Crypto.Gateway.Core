using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

/// <summary>
/// Mints a brand-new HD wallet for a merchant: creates a fresh seed, exports and stores its account xpub in
/// the secret store, and returns an <b>un-persisted</b> <see cref="HdWallet"/> (MerchantId set) for the
/// caller to save inside the create-on-first-use transaction.
///
/// Separate seed per merchant is a deliberate custody choice (§10): one merchant's key compromise cannot
/// expose another merchant's addresses. The seed never crosses this boundary — only the wallet record, which
/// holds references, and the public xpub, which the store already keeps. Development mints in-memory;
/// production mints inside a KMS/HSM behind the same port (a separate implementation, deferred).
/// </summary>
public interface IHdWalletProvisioner
{
    Task<Result<HdWallet>> ProvisionMerchantDepositWalletAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default);
}
