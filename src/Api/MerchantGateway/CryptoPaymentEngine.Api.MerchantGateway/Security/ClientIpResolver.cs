using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.MerchantGateway.Security;

/// <summary>Which reverse proxy this host trusts to report the original caller. Bound from <c>Gateway:ClientIp</c>.</summary>
public sealed class ClientIpOptions
{
    public const string SectionName = "Gateway:ClientIp";

    /// <summary>The header the trusted proxy writes the caller's address into.</summary>
    public string ClientIpHeader { get; init; } = "CF-Connecting-IP";

    /// <summary>
    /// CIDR ranges the proxy connects from (Cloudflare's published ranges). Empty by default, deliberately: with no
    /// proxy trusted, the caller is the TCP peer and no header is ever believed. The list lives in configuration
    /// rather than as a code default because .NET binds a configured array by adding to a code default.
    /// </summary>
    public string[] TrustedProxyRanges { get; init; } = [];
}

/// <summary>
/// Works out which address a merchant API call really came from, for the per-merchant IP allowlist.
///
/// <para>Behind Cloudflare every connection arrives from a Cloudflare edge, and the caller's own address travels in
/// <c>CF-Connecting-IP</c>. That is an ordinary request header: anyone who reaches the origin directly can send it
/// with any value. So it is believed <b>only</b> when the connection itself comes from a configured proxy range.
/// Any other connection is judged by its own TCP address, which cannot be forged.</para>
///
/// <para>A connection from a trusted proxy that carries no single readable address returns <c>null</c>. The real
/// caller is then unknown, and an unknown caller is refused rather than attributed to the proxy.</para>
/// </summary>
public sealed class ClientIpResolver
{
    private readonly System.Net.IPNetwork[] _trustedProxies;
    private readonly string _header;

    public ClientIpResolver(IOptions<ClientIpOptions> options)
    {
        _header = options.Value.ClientIpHeader;
        _trustedProxies = options.Value.TrustedProxyRanges
            .Select(range => range.Trim())
            .Where(range => range.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(range => System.Net.IPNetwork.TryParse(range, out var network)
                ? network
                : throw new InvalidOperationException(
                    $"{ClientIpOptions.SectionName}:TrustedProxyRanges contains '{range}', which is not a CIDR range."))
            .ToArray();
    }

    public IPAddress? Resolve(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } connection)
            return null;

        var peer = Normalize(connection);
        if (!_trustedProxies.Any(network => network.Contains(peer)))
            return peer;

        var reported = context.Request.Headers[_header];
        if (reported.Count != 1)
            return null;

        var value = reported[0]?.Trim();
        return !string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out var caller)
            ? Normalize(caller)
            : null; // missing, a list, or not an address: the caller is unknown
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }
}
