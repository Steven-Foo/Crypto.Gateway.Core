namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

/// <summary>
/// How child keys are derived. This is not cosmetic: ed25519 (SLIP-0010) supports
/// <b>hardened derivation only</b>, so a Solana address cannot be derived from a public key —
/// it needs the seed. secp256k1 (BIP-32) supports non-hardened <c>CKDpub</c>, so its addresses
/// derive from an account xpub with no access to key material at all.
/// </summary>
public enum DerivationScheme
{
    /// <summary>BIP-32 over secp256k1. Tron, Ethereum. Watch-only derivation from an xpub.</summary>
    Bip32Secp256k1 = 1,

    /// <summary>SLIP-0010 over ed25519. Solana. Hardened-only: seed access is unavoidable.</summary>
    Slip10Ed25519 = 2,
}

public enum HdWalletPurpose
{
    Deposit = 1,
    Withdrawal = 2,
    Treasury = 3,
    Energy = 4,
    Cold = 5,
}

public enum HdWalletStatus
{
    Active = 1,
    Archived = 2,
    Disabled = 3,
}

/// <summary>
/// Where the seed lives. Never the database, never configuration, never source (§10).
/// <see cref="InMemoryDevelopment"/> exists solely for tests and must never be used in production.
/// </summary>
public enum SecretProviderKind
{
    AwsSecretsManager = 1,
    AzureKeyVault = 2,
    HashiCorpVault = 3,
    Hsm = 4,
    InMemoryDevelopment = 99,
}
