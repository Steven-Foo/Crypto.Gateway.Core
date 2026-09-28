namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

/// <summary>
/// The audit-log text for a 2FA-policy change, shared by both hosts that save one (the admin back office for
/// the platform minimum, the portal for a merchant's own additions).
///
/// <para>Records what CHANGED (added / removed) rather than both full lists, and is capped to the audit column:
/// writing both lists overflowed the 512-character <c>audit.AuditEntry.Reason</c> on the staff side, failing
/// the audit write after the policy had already saved. The complete lists are always in the append-only
/// policy history.</para>
/// </summary>
public static class MerchantTwoFactorPolicyAudit
{
    public const int ReasonMaxLength = 512;

    public static string Describe(
        IReadOnlyCollection<string> before, IReadOnlyCollection<string> after, string? note)
    {
        var added = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var removed = before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var text = added.Count == 0 && removed.Count == 0
            ? "2FA actions saved unchanged."
            : "2FA actions changed." +
              (added.Count > 0 ? $" Added: {string.Join(", ", added)}." : "") +
              (removed.Count > 0 ? $" Removed: {string.Join(", ", removed)}." : "");

        if (!string.IsNullOrWhiteSpace(note))
            text += $" Note: {note.Trim()}";

        const string suffix = " ... (truncated; full list in the 2FA policy history)";
        return text.Length <= ReasonMaxLength ? text : text[..(ReasonMaxLength - suffix.Length)] + suffix;
    }
}
