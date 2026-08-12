using System.Text;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// The in-memory resource-operation builder (Energy 5b dev seam). It must produce a stable, non-empty blob and
/// distinguish stake from delegate, so the in-memory signer/broadcaster carry the whole lifecycle in dev.
/// </summary>
public sealed class InMemoryResourceOperationBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly InMemoryResourceOperationBuilder _builder = new();

    [Fact]
    public async Task Stake_and_delegate_build_distinct_non_empty_blobs()
    {
        var stake = await _builder.BuildStakeForEnergyAsync(new StakeForEnergyRequest(Chain.Tron, "TStaker", 1_000_000), Ct);
        var delegate1 = await _builder.BuildDelegateEnergyAsync(new DelegateEnergyRequest(Chain.Tron, "TStaker", "TDeposit", 500_000), Ct);

        stake.Payload.Length.ShouldBeGreaterThan(0);
        delegate1.Payload.Length.ShouldBeGreaterThan(0);
        stake.Payload.ShouldNotBe(delegate1.Payload);
        Encoding.UTF8.GetString(delegate1.Payload).ShouldContain("TDeposit"); // the receiver is captured
    }
}
