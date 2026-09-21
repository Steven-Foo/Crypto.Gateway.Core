using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// The one definition of "an IP address a merchant may allowlist", and of when two addresses are the same.
///
/// <para><b>Exact addresses only:</b> no CIDR ranges, ports, zone ids or host names. The allowlist names the
/// servers that hold a merchant's signing secret, and a range is a decision to trust every address in it.</para>
///
/// <para><b>Normalised before it is stored and before it is compared.</b> <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>
/// accepts text that reads as something else: <c>1.2</c> is <c>1.0.0.2</c>, <c>010.0.0.1</c> is octal, and an IPv4
/// caller can arrive as <c>::ffff:1.2.3.4</c>. Comparing strings would let a server's address fail to match itself,
/// and accepting shorthand would store an address nobody typed. So IPv4 must be four decimal octets, an IPv4-mapped
/// IPv6 address counts as its IPv4 address, and IPv6 is kept in its canonical compressed lowercase form.</para>
/// </summary>
public static partial class MerchantIpAddress
{
    /// <summary>Parses <paramref name="raw"/> as a single, fully written IP address and returns its canonical text.</summary>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (value.IndexOfAny(['/', '%', '[', ']', ' ']) >= 0)
            return false; // a range, a zone id, a bracketed host:port, or two values

        if (!IPAddress.TryParse(value, out var address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork && !DottedQuad().IsMatch(value))
            return false; // shorthand, octal or hex octets: parses, but not as what was typed

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !value.Contains(':'))
            return false;

        normalized = Normalize(address).ToString();
        return true;
    }

    /// <summary>
    /// The form two addresses are compared in: an IPv4-mapped IPv6 address becomes IPv4, and an IPv6 zone id is
    /// dropped (it names a local interface, not a host).
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$")]
    private static partial Regex DottedQuad();
}
