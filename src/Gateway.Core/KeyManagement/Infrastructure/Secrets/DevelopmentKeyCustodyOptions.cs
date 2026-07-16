namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Binds the dev HD-wallet seed rows and the in-memory secret values from
/// configuration (section <c>KeyManagement</c>), so a developer can point local deposit provisioning at
/// any xpub — including a real branch xpub copied from production — without touching code. The committed
/// default carries a published BIP-39 test vector; real values go in a git-ignored
/// <c>appsettings.Local.json</c>, which overrides these at the same keys.
///
/// <b>Public material only.</b> <see cref="DevSecrets"/> holds account/branch <c>xpub</c> strings, which
/// are public data. It must NEVER hold a mnemonic, seed, or private key (§10): the watch-only provisioning
/// path reads only the xpub (see <c>WalletDerivationService.GetAccountPublicKeyAsync</c>), so a seed here
/// would be both unused and a violation.
/// </summary>
public sealed class DevelopmentKeyCustodyOptions
{
    public const string SectionName = "KeyManagement";

    /// <summary>HD wallets the dev seeder creates at startup when an active one does not already exist.</summary>
    public List<DevWalletSeed> DevWallets { get; init; } = [];

    /// <summary>Secret-store reference → xpub string, served by the in-memory <c>ISecretProvider</c>.</summary>
    public Dictionary<string, string> DevSecrets { get; init; } = new();

    /// <summary>
    /// OPTIONAL. A real TRON account xpub at <c>m/44'/195'/0'/0</c> (the change level, same convention as
    /// <see cref="DevSecrets"/>). When set, the dev provisioner derives merchant deposit addresses from THIS
    /// (public) key instead of the throwaway per-merchant dev seed — so addresses live in the developer's own
    /// wallet tree and any test funds are recoverable there. REQUIRED before sending real mainnet funds: the
    /// fallback seed's salt is public in this repo. Public xpub only — never a seed/mnemonic (§10).
    /// Single-test-merchant only: two merchants sharing one xpub would derive colliding addresses.
    /// </summary>
    public string? DevMerchantXpub { get; init; }
}

/// <summary>One HD wallet the dev seeder will materialise. Mirrors the arguments of <c>HdWallet.Create</c>.</summary>
public sealed class DevWalletSeed
{
    public string Name { get; init; } = string.Empty;

    /// <summary>A <c>Chain</c> name, e.g. <c>Tron</c>. Parsed case-insensitively.</summary>
    public string Chain { get; init; } = string.Empty;

    /// <summary>An <c>HdWalletPurpose</c> name, e.g. <c>Deposit</c>. Parsed case-insensitively.</summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>e.g. <c>m/44'/195'/0'/0</c>.</summary>
    public string DerivationPath { get; init; } = string.Empty;

    /// <summary>Reference to the seed in the secret store. Stored on the row, never dereferenced for watch-only derivation.</summary>
    public string SecretReference { get; init; } = string.Empty;

    /// <summary>Reference to the account/branch xpub. Required for secp256k1 (Tron/Ethereum) watch-only wallets.</summary>
    public string? PublicKeyReference { get; init; }
}
