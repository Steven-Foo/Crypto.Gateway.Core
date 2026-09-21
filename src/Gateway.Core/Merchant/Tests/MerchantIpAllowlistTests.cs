using System.Net;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// The merchant API IP allowlist: what counts as an allowed address, when two addresses are the same, and that an
/// empty list permits nothing.
/// </summary>
public sealed class MerchantIpAllowlistTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);

    private static MerchantEntity NewMerchant() => MerchantEntity.Create("IPS-1", "Allowlist", null).Value;

    private static MerchantEntity MerchantAllowing(params string[] ips)
    {
        var merchant = NewMerchant();
        merchant.UpdateAllowedIps(ips, Now).IsSuccess.ShouldBeTrue();
        return merchant;
    }

    [Theory]
    [InlineData("203.0.113.10", "203.0.113.10")]
    [InlineData("  203.0.113.10 ", "203.0.113.10")]
    [InlineData("2001:DB8::10", "2001:db8::10")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0010", "2001:db8::10")]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10")]
    [InlineData("::1", "::1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    public void A_single_full_address_is_accepted_in_its_canonical_form(string raw, string expected)
    {
        MerchantIpAddress.TryNormalize(raw, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("203.0.113.0/24")]        // a range
    [InlineData("2001:db8::/32")]         // an IPv6 range
    [InlineData("203.0.113.10:443")]      // host:port
    [InlineData("[2001:db8::10]:443")]    // bracketed host:port
    [InlineData("fe80::1%3")]             // a zone id
    [InlineData("1.2")]                   // shorthand that parses as 1.0.0.2
    [InlineData("010.0.0.1")]             // octal octet
    [InlineData("0x7f.0.0.1")]            // hex octet
    [InlineData("256.1.1.1")]
    [InlineData("203.0.113.10 198.51.100.7")]
    [InlineData("api.merchant.example")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_but_a_single_full_address_is_refused(string raw)
    {
        MerchantIpAddress.TryNormalize(raw, out _).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_allowlist_permits_no_caller()
    {
        NewMerchant().AllowsApiCallFrom(IPAddress.Parse("203.0.113.10")).ShouldBeFalse();
    }

    [Fact]
    public void A_listed_address_is_permitted_and_any_other_is_not()
    {
        var merchant = MerchantAllowing("203.0.113.10", "2001:db8::10");

        merchant.AllowsApiCallFrom(IPAddress.Parse("203.0.113.10")).ShouldBeTrue();
        merchant.AllowsApiCallFrom(IPAddress.Parse("2001:db8::10")).ShouldBeTrue();
        merchant.AllowsApiCallFrom(IPAddress.Parse("203.0.113.11")).ShouldBeFalse();
        merchant.AllowsApiCallFrom(IPAddress.Parse("2001:db8::11")).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_caller_is_refused_even_with_a_populated_allowlist()
    {
        MerchantAllowing("203.0.113.10").AllowsApiCallFrom(null).ShouldBeFalse();
    }

    [Fact]
    public void An_ipv4_caller_arriving_as_a_mapped_ipv6_address_matches_its_ipv4_entry()
    {
        MerchantAllowing("203.0.113.10").AllowsApiCallFrom(IPAddress.Parse("::ffff:203.0.113.10")).ShouldBeTrue();
    }

    [Fact]
    public void Ipv6_matches_however_it_was_written_and_is_stored_canonically()
    {
        var merchant = MerchantAllowing("2001:0DB8:0:0::10");

        merchant.Configuration.AllowedIps.ShouldBe(["2001:db8::10"]);
        merchant.AllowsApiCallFrom(IPAddress.Parse("2001:db8:0:0:0:0:0:10")).ShouldBeTrue();
    }

    [Fact]
    public void An_update_with_any_refused_entry_fails_and_leaves_the_allowlist_unchanged()
    {
        var merchant = MerchantAllowing("203.0.113.10");

        var result = merchant.UpdateAllowedIps(["203.0.113.11", "203.0.113.0/24"], Now);

        result.Error!.Code.ShouldBe(MerchantErrors.InvalidIpAddress.Code);
        merchant.Configuration.AllowedIps.ShouldBe(["203.0.113.10"]);
    }

    [Fact]
    public void A_range_stored_before_ranges_were_refused_matches_nothing()
    {
        // The portal used to accept CIDR ranges. A range already in the database must not quietly become a match
        // under the exact-address rule, nor be guessed at.
        var merchant = NewMerchant();
        typeof(MerchantConfiguration).GetProperty(nameof(MerchantConfiguration.AllowedIpsCsv))!
            .SetValue(merchant.Configuration, "203.0.113.0/24");

        merchant.AllowsApiCallFrom(IPAddress.Parse("203.0.113.10")).ShouldBeFalse();
    }

    [Fact]
    public void Partition_normalises_and_deduplicates_valid_entries_and_quotes_refused_ones_as_typed()
    {
        var (valid, invalid) = AllowedIpInput.Partition(
            ["203.0.113.10", " 203.0.113.10", "::FFFF:203.0.113.10", "203.0.113.0/24", "", null, "2001:DB8::1"]);

        valid.ShouldBe(["203.0.113.10", "2001:db8::1"]);
        invalid.ShouldBe(["203.0.113.0/24"]);
    }

    [Fact]
    public void The_domain_error_code_is_the_one_the_contract_publishes()
    {
        MerchantErrors.IpNotAllowed.Code.ShouldBe(MerchantRequestVerificationErrors.IpNotAllowed);
    }
}
