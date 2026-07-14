namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Server-side peppers, keyed by version. These are secrets: they belong in a KMS/secret store and
/// must never be written to the database, source, or logs (§10). Keeping old versions here is what
/// lets an existing credential keep verifying after a pepper rotation.
/// </summary>
public sealed class ApiCredentialOptions
{
    public const string SectionName = "Merchant:ApiCredentials";

    public int CurrentHashVersion { get; init; } = 1;

    /// <summary>version -> pepper (base64 or raw). Must contain <see cref="CurrentHashVersion"/>.</summary>
    public IReadOnlyDictionary<int, string> Peppers { get; init; } = new Dictionary<int, string>();
}
