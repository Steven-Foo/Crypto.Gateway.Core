using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

/// <summary>
/// A technical persistence row (not a domain entity — it holds no business behaviour, §15.3) storing one HD
/// wallet's KMS-envelope material: the encrypted seed <see cref="Ciphertext"/> and the public account
/// <see cref="Xpub"/>, together, keyed by an opaque <see cref="Reference"/>. There is deliberately no plaintext
/// seed column and no private-key column — only ciphertext and public material (§10). A unique index on
/// <see cref="Reference"/> makes seed generation exactly-once under concurrency.
/// </summary>
public sealed class SecretMaterial
{
    public Guid Id { get; private set; }
    public string Reference { get; private set; } = null!;

    /// <summary>The seed sealed by a KMS CMK (envelope encryption). Never the plaintext seed.</summary>
    public byte[] Ciphertext { get; private set; } = null!;

    /// <summary>The public account xpub — public data, so stored in the clear for watch-only derivation (§8).</summary>
    public string Xpub { get; private set; } = null!;

    /// <summary>The CMK ARN that sealed <see cref="Ciphertext"/>; used (and validated) at decrypt time.</summary>
    public string KmsKeyId { get; private set; } = null!;

    public HdWalletPurpose Purpose { get; private set; }
    public Chain Chain { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SecretMaterial() { }

    public SecretMaterial(
        string reference, byte[] ciphertext, string xpub, string kmsKeyId,
        HdWalletPurpose purpose, Chain chain, DateTimeOffset createdAt)
    {
        Id = Guid.CreateVersion7();
        Reference = reference;
        Ciphertext = ciphertext;
        Xpub = xpub;
        KmsKeyId = kmsKeyId;
        Purpose = purpose;
        Chain = chain;
        CreatedAt = createdAt;
    }
}
