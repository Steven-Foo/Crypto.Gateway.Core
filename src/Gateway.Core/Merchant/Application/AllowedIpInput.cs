using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// Splits submitted allowlist entries into the ones the Merchant module will accept and the ones it will refuse,
/// by the module's own rule (<see cref="MerchantIpAddress"/>). Edit screens use it to report exactly which entries
/// were refused, without carrying a second, drifting idea of what a valid address is.
/// </summary>
public static class AllowedIpInput
{
    /// <summary>
    /// Valid entries come back normalised and de-duplicated, in the order first given; refused entries come back
    /// exactly as typed (trimmed), so the message can quote them. Blank entries are ignored.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) Partition(IEnumerable<string?> entries)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var trimmed = entry?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                continue;

            if (!MerchantIpAddress.TryNormalize(trimmed, out var normalized))
                invalid.Add(trimmed);
            else if (seen.Add(normalized))
                valid.Add(normalized);
        }

        return (valid, invalid);
    }
}
