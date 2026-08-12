using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Configuration;

/// <summary>
/// Builds each chain's <see cref="SweepPolicy"/> from config <c>Sweep:Policies</c>. All amounts are exact
/// base-unit integers (never display values — §14). A missing policy throws on lookup: the system must never
/// sweep against an unconfigured threshold. A per-asset (rather than per-chain) policy is a future refinement.
/// </summary>
public sealed class ConfigurationSweepPolicyProvider : ISweepPolicyProvider
{
    private readonly IReadOnlyDictionary<Chain, SweepPolicy> _policies;

    public ConfigurationSweepPolicyProvider(IConfiguration configuration)
    {
        var policies = new Dictionary<Chain, SweepPolicy>();

        foreach (var child in configuration.GetSection("Sweep:Policies").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            var minSweep = BigInteger.Parse(child["MinSweepAmountBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
            var confirmations = int.Parse(child["Confirmations"] ?? "0", CultureInfo.InvariantCulture);

            policies[chain] = new SweepPolicy(minSweep, confirmations);
        }

        _policies = policies;
    }

    public SweepPolicy For(Chain chain) =>
        _policies.TryGetValue(chain, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No sweep policy configured for {chain}. Add 'Sweep:Policies:{chain}'.");
}
