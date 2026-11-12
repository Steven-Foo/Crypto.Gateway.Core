namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;

/// <summary>
/// What a host may do with KMS custody. Chosen in code by the composition root, never by configuration — a
/// settings-file edit must not be able to turn a back-office host into one that can unseal seeds (§10).
/// </summary>
public enum KmsKeyAccess
{
    /// <summary>
    /// Read public xpubs and mint + seal NEW seeds (<c>kms:Encrypt</c>), but never decrypt one. For the back-office
    /// hosts: they create deposit addresses (a new merchant, a merchant top-up) and never sign anything. A
    /// signing reference (<c>"{ref}#{index}"</c>) is refused before any KMS call is made.
    /// </summary>
    AddressOnly,

    /// <summary>Everything, including <c>kms:Decrypt</c> to derive a signing key. The money host only.</summary>
    Signing,
}

/// <summary>The access mode registered for the process — a singleton the secret provider consults.</summary>
public sealed record KmsKeyAccessMode(KmsKeyAccess Access);
