using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;

/// <summary>What custody hands back once a platform key is registered. <see cref="KeyReference"/> is a
/// reference, never key material (§10).</summary>
public sealed record RegisteredPlatformKey(Guid DerivedKeyId, Chain Chain, string Address, string KeyReference);

/// <summary>
/// Registers a platform (non-merchant) key whose material was <b>imported directly</b> — not HD-derived —
/// e.g. a fixed dev/testnet throwaway key, or later a production key imported into a KMS. Idempotent:
/// re-registering the same <c>(chain, purpose, address)</c> returns the existing registration; a
/// <em>different</em> address for an already-active wallet of that chain/purpose is a conflict and is
/// refused (a signing key is never silently reassigned).
///
/// The production implementation is deliberately not built in this cut (§10 seam): only a dev/testnet
/// implementation exists, registered alongside the in-memory secret provider it depends on. Production
/// would generate/hold the key inside a KMS and expose the reference through a separate implementation.
/// </summary>
public interface IPlatformKeyRegistrar
{
    Task<Result<RegisteredPlatformKey>> RegisterImportedKeyAsync(
        Chain chain,
        DerivationPurpose purpose,
        string address,
        string secretReference,
        string? description = null,
        CancellationToken cancellationToken = default);
}
