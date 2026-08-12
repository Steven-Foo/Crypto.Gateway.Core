using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;

/// <summary>
/// Configuration for the production AWS KMS envelope custody. Carries only <b>identifiers</b> — a region and
/// key ARNs — never secrets (§10); the app authenticates to AWS by its instance/task IAM role, so no access
/// keys live in config. One customer-managed symmetric CMK per purpose (deposit-sweep vs withdrawal-pool),
/// so a compromise or IAM-policy mistake on one purpose's key cannot reach the other's seeds.
/// </summary>
public sealed class AwsKmsKeyCustodyOptions
{
    public const string SectionName = "KeyManagement:Kms";

    /// <summary>Master switch. When false, no KMS provider/provisioner is registered and custody stays inert.</summary>
    public bool Enabled { get; set; }

    /// <summary>AWS region system name, e.g. <c>ap-southeast-1</c>. An identifier, not a secret.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Per-purpose CMK ARNs. An identifier, not a secret.</summary>
    public KmsKeyArns KeyArns { get; set; } = new();

    /// <summary>The CMK that seals seeds for a given wallet purpose, or null when that purpose is unconfigured.</summary>
    public string? KeyArnFor(HdWalletPurpose purpose) => purpose switch
    {
        HdWalletPurpose.Deposit => NullIfBlank(KeyArns.Deposit),
        HdWalletPurpose.Withdrawal => NullIfBlank(KeyArns.Withdrawal),
        _ => null,
    };

    /// <summary>True when <paramref name="keyArn"/> is one of the configured CMKs — the decrypt-side guard that
    /// refuses to point <c>kms:Decrypt</c> at a key reference this system was not configured to trust.</summary>
    public bool IsConfiguredKey(string? keyArn) =>
        !string.IsNullOrWhiteSpace(keyArn) &&
        (string.Equals(keyArn, NullIfBlank(KeyArns.Deposit), StringComparison.Ordinal) ||
         string.Equals(keyArn, NullIfBlank(KeyArns.Withdrawal), StringComparison.Ordinal));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed class KmsKeyArns
    {
        public string? Deposit { get; set; }
        public string? Withdrawal { get; set; }
    }
}
