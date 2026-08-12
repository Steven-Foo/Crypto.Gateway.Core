using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

/// <summary>
/// One HD wallet's protected material, addressed by an opaque <see cref="Reference"/>. A single row carries
/// <b>both</b> the KMS-sealed seed <see cref="Ciphertext"/> and the public account <see cref="Xpub"/>, so the
/// pairing is arbitrated atomically (a torn write that mated one wallet's ciphertext with another's xpub would
/// silently derive addresses no key can sign). The seed plaintext is never stored — only its ciphertext (§10).
/// </summary>
public sealed record StoredSecretMaterial(
    string Reference,
    byte[] Ciphertext,
    string Xpub,
    string KmsKeyId,
    HdWalletPurpose Purpose,
    Chain Chain);

/// <summary>
/// The backing store for envelope-encrypted HD-wallet material: the "my database" half of the two-factor
/// custody model (ciphertext here, key-encryption-key in KMS — either alone useless). Write is
/// insert-once/adopt-on-conflict so a fresh seed is generated <b>exactly once</b> per wallet even under a
/// create-on-first-use race, and a crash between material-write and wallet-write self-heals on retry.
/// </summary>
public interface ISecretMaterialStore
{
    Task<StoredSecretMaterial?> GetAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts <paramref name="material"/>, or — if a row with the same <see cref="StoredSecretMaterial.Reference"/>
    /// already exists (a concurrent provisioning won the race, or this is a retry) — returns the already-stored
    /// row unchanged. The unique reference index is the arbiter, exactly like the HD-wallet create-on-first-use
    /// race: whoever wrote first defines the seed, and every other caller adopts it.
    /// </summary>
    Task<StoredSecretMaterial> GetOrAddAsync(StoredSecretMaterial material, CancellationToken cancellationToken = default);
}
