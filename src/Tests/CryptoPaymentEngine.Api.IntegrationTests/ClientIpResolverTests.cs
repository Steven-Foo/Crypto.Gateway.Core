using System.Net;
using CryptoPaymentEngine.Api.MerchantGateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Api.IntegrationTests;

/// <summary>
/// Which address the merchant IP allowlist judges. The load-bearing property: <c>CF-Connecting-IP</c> is believed only
/// on a connection from a trusted proxy range, so a caller who reaches the origin directly cannot claim to be an
/// allowlisted server.
/// </summary>
public sealed class ClientIpResolverTests
{
    private static readonly string[] Cloudflare = ["173.245.48.0/20", "104.16.0.0/13", "2606:4700::/32"];

    private static ClientIpResolver Resolver(params string[] trustedRanges) =>
        new(Options.Create(new ClientIpOptions { TrustedProxyRanges = trustedRanges }));

    private static HttpContext Request(string peer, params string[] clientHeader)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (clientHeader.Length > 0)
            context.Request.Headers["CF-Connecting-IP"] = clientHeader;
        return context;
    }

    [Fact]
    public void Through_cloudflare_the_caller_is_the_address_cloudflare_reports()
    {
        Resolver(Cloudflare).Resolve(Request("104.21.24.54", "203.0.113.10")).ShouldBe(IPAddress.Parse("203.0.113.10"));
    }

    [Fact]
    public void A_header_sent_from_outside_the_trusted_ranges_is_ignored()
    {
        // Someone reaching the origin directly, claiming to be an allowlisted server.
        Resolver(Cloudflare).Resolve(Request("198.51.100.7", "203.0.113.10")).ShouldBe(IPAddress.Parse("198.51.100.7"));
    }

    [Fact]
    public void With_no_trusted_proxy_configured_the_header_is_never_believed()
    {
        Resolver().Resolve(Request("104.21.24.54", "203.0.113.10")).ShouldBe(IPAddress.Parse("104.21.24.54"));
    }

    [Fact]
    public void Through_a_trusted_proxy_without_the_header_the_caller_is_unknown()
    {
        Resolver(Cloudflare).Resolve(Request("104.21.24.54")).ShouldBeNull();
    }

    [Theory]
    [InlineData("203.0.113.10, 198.51.100.7")]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void Through_a_trusted_proxy_an_unreadable_header_leaves_the_caller_unknown(string header)
    {
        Resolver(Cloudflare).Resolve(Request("104.21.24.54", header)).ShouldBeNull();
    }

    [Fact]
    public void Two_copies_of_the_header_leave_the_caller_unknown()
    {
        Resolver(Cloudflare).Resolve(Request("104.21.24.54", "203.0.113.10", "198.51.100.7")).ShouldBeNull();
    }

    [Fact]
    public void An_ipv6_cloudflare_edge_is_trusted_too()
    {
        Resolver(Cloudflare).Resolve(Request("2606:4700::6810:1836", "2001:db8::10")).ShouldBe(IPAddress.Parse("2001:db8::10"));
    }

    [Fact]
    public void A_mapped_ipv4_connection_is_judged_as_ipv4()
    {
        Resolver(Cloudflare).Resolve(Request("::ffff:104.21.24.54", "203.0.113.10")).ShouldBe(IPAddress.Parse("203.0.113.10"));
        Resolver(Cloudflare).Resolve(Request("::ffff:198.51.100.7", "203.0.113.10")).ShouldBe(IPAddress.Parse("198.51.100.7"));
    }

    [Fact]
    public void A_connection_with_no_address_is_unknown()
    {
        Resolver(Cloudflare).Resolve(new DefaultHttpContext()).ShouldBeNull();
    }

    [Fact]
    public void A_malformed_proxy_range_refuses_to_construct()
    {
        Should.Throw<InvalidOperationException>(() => Resolver("104.16.0.0/99")).Message.ShouldContain("104.16.0.0/99");
    }
}
