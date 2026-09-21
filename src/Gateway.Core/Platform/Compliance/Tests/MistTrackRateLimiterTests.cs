using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// Pacing has to hold across the whole process, not just inside one dependency-injection scope.
///
/// <para>The provider is registered as a typed <see cref="HttpClient"/> consumer, which makes it
/// <b>transient</b>. When the pacing state lived on the provider, every scope got a fresh gate starting
/// from the beginning of time: correct inside a single worker pass, and no constraint whatsoever between
/// passes, between a worker and an HTTP request, or between two workers in the same process. Each caller
/// paced itself perfectly while the process breached the limit by the number of concurrent callers.</para>
///
/// <para>That matters because the provider answers a breach with 429, a 429 is a screening with no verdict,
/// and the payout gate holds a no-verdict payout for staff. An unpaced burst becomes a queue of held
/// payouts.</para>
/// </summary>
public sealed class MistTrackRateLimiterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildContainer() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddComplianceModuleWithMistTrack(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Compliance:MistTrack:ApiKey"] = "k-123",
                        ["Compliance:MistTrack:BaseUrl"] = "https://openapi.misttrack.io",

                        // Zero disables pacing, so nothing in this test class ever waits on a real interval.
                        ["Compliance:MistTrack:RequestsPerSecond"] = "0",
                    })
                    .Build(),
                // Never connected to: the registration builds a DbContext option, it does not open anything.
                "Server=(localdb)\\MSSQLLocalDB;Database=CpeComplianceLimiterTests;Trusted_Connection=True")
            .BuildServiceProvider();

    /// <summary>
    /// The load-bearing assertion. Two scopes MUST share one gate. If this fails, every screening caller is
    /// pacing against its own private clock and the plan's per-second limit is not being honoured.
    /// </summary>
    [Fact]
    public void Separate_scopes_share_one_pacing_gate()
    {
        using var container = BuildContainer();

        using var first = container.CreateScope();
        using var second = container.CreateScope();

        var a = first.ServiceProvider.GetRequiredService<MistTrackRateLimiter>();
        var b = second.ServiceProvider.GetRequiredService<MistTrackRateLimiter>();

        a.ShouldBeSameAs(b);
    }

    /// <summary>
    /// Documents the fact that made the bug possible, so nobody "simplifies" the limiter back onto the
    /// provider. A typed HttpClient registration is transient by design — the point is that the pacing must
    /// NOT live on something with that lifetime.
    /// </summary>
    [Fact]
    public void The_provider_itself_is_transient_which_is_why_the_gate_cannot_live_on_it()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();

        var a = scope.ServiceProvider.GetRequiredService<IAddressRiskProvider>();
        var b = scope.ServiceProvider.GetRequiredService<IAddressRiskProvider>();

        a.ShouldNotBeSameAs(b);
        a.ShouldBeOfType<MistTrackAddressRiskProvider>();
    }

    [Fact]
    public void Production_composition_resolves_the_real_provider_and_not_the_fake()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();

        // A fabricated clean score is indistinguishable from a real one, so the in-memory provider must
        // never be reachable from a composition that registered the real adapter (§10).
        scope.ServiceProvider.GetRequiredService<IAddressRiskProvider>()
            .Name.ShouldBe("MistTrack");
    }

    [Fact]
    public async Task A_disabled_rate_returns_immediately_rather_than_dividing_by_zero()
    {
        var limiter = new MistTrackRateLimiter(
            Options.Create(new MistTrackOptions { RequestsPerSecond = 0 }), new TestClock());

        // Would hang or throw if the zero were fed into the interval calculation.
        await limiter.WaitAsync(Ct);
    }

    /// <summary>
    /// A second call does not go straight through — it is scheduled behind the interval.
    ///
    /// <para>Uses a deliberately fast rate so the real wait is milliseconds. <c>TestClock</c> does not
    /// override <c>CreateTimer</c>, so the delay inside the limiter runs on real time regardless of what the
    /// clock says; advancing a fake clock here would assert nothing while appearing to.</para>
    /// </summary>
    [Fact]
    public async Task A_second_call_is_scheduled_behind_the_interval_rather_than_going_straight_through()
    {
        var limiter = new MistTrackRateLimiter(
            Options.Create(new MistTrackOptions { RequestsPerSecond = 50 }), TimeProvider.System);

        await limiter.WaitAsync(Ct);

        var second = limiter.WaitAsync(Ct);
        second.IsCompleted.ShouldBeFalse("the second call must be held back by the pacing interval");

        await second;
    }
}
