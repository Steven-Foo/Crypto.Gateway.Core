using CryptoPaymentEngine.Api.MerchantGateway.Endpoints;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Api.IntegrationTests;

/// <summary>
/// The frozen merchant API publishes exactly three withdrawal statuses — pending / confirmed / failed — and a
/// merchant's integration is written against those. This maps the internal lifecycle onto them.
///
/// <para><b>Why this is tested:</b> the mapping has a catch-all that returns "pending". That is the right
/// default for a genuinely in-flight state, but it means <em>any</em> new terminal status added later is
/// silently reported as pending forever — telling a merchant their money is still on its way after it has
/// arrived. Nothing else in the system would catch that, because it is a correct-looking string. This test
/// enumerates every status the domain can produce, so a new one must be classified deliberately.</para>
/// </summary>
public sealed class MerchantApiStatusMappingTests
{
    /// <summary>The three values the frozen contract may ever emit.</summary>
    private static readonly string[] Published = ["pending", "confirmed", "failed"];

    /// <summary>Terminal states where the merchant's money has genuinely moved.</summary>
    private static readonly WithdrawalStatus[] Succeeded =
    [
        WithdrawalStatus.Confirmed,      // the platform built, signed and broadcast it
        WithdrawalStatus.FinanceSettled, // an admin paid it off-system and we verified the hash
    ];

    /// <summary>Terminal states where the withdrawal will not happen and the reserve was released.</summary>
    private static readonly WithdrawalStatus[] Ended =
    [
        WithdrawalStatus.Rejected,
        WithdrawalStatus.Failed,
    ];

    [Fact]
    public void Every_domain_status_maps_to_a_published_contract_value()
    {
        foreach (var status in Enum.GetValues<WithdrawalStatus>())
            Published.ShouldContain(
                MerchantApiEndpoints.MapWithdrawalStatus(status.ToString()),
                $"status '{status}' must map to a value the frozen contract publishes");
    }

    [Fact]
    public void Both_successful_terminal_states_report_confirmed()
    {
        // FinanceSettled is the one that used to fall through to "pending": a merchant cash-out paid by an
        // admin outside platform custody is complete, and the API must say so.
        foreach (var status in Succeeded)
            MerchantApiEndpoints.MapWithdrawalStatus(status.ToString())
                .ShouldBe("confirmed", $"'{status}' is a terminal success");
    }

    [Fact]
    public void Terminal_failures_report_failed()
    {
        foreach (var status in Ended)
            MerchantApiEndpoints.MapWithdrawalStatus(status.ToString()).ShouldBe("failed", $"'{status}' ended");
    }

    [Fact]
    public void Everything_still_in_flight_reports_pending()
    {
        // Including the two merchant-settlement stages that need a human: from the merchant's point of view
        // an audit or a finance transfer that has not happened yet is simply still pending.
        var inFlight = Enum.GetValues<WithdrawalStatus>()
            .Except(Succeeded)
            .Except(Ended);

        foreach (var status in inFlight)
            MerchantApiEndpoints.MapWithdrawalStatus(status.ToString())
                .ShouldBe("pending", $"'{status}' has not reached a terminal state");
    }
}
