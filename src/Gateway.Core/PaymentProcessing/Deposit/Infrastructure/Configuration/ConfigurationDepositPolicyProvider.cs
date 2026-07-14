using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Configuration;

/// <summary>
/// Builds each chain's <see cref="DepositPolicy"/> from configuration section <c>Deposit:Policies</c>.
///
/// <para><b>MinDepositBaseUnits is an exact integer</b> in the asset's base units — deliberately not a
/// display value like <c>0.001</c>. A per-chain display floor is ambiguous across assets (0.001 ETH at
/// 18 dp vs 1 USDT at 6 dp) and would need float/decimals conversion; base-unit integers are exact and
/// honour §14. A per-asset dust floor is a future refinement.</para>
///
/// A chain with no configured policy throws on lookup — the system must never credit against an
/// unconfigured confirmation depth (no silent money default).
/// </summary>
public sealed class ConfigurationDepositPolicyProvider : IDepositPolicyProvider
{
    private readonly IReadOnlyDictionary<Chain, DepositPolicy> _policies;

    public ConfigurationDepositPolicyProvider(IConfiguration configuration)
    {
        var policies = new Dictionary<Chain, DepositPolicy>();

        foreach (var child in configuration.GetSection("Deposit:Policies").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            var strategy = Enum.Parse<CreditStrategy>(child["CreditStrategy"] ?? nameof(CreditStrategy.Confirmations), ignoreCase: true);
            var confirmations = int.Parse(child["Confirmations"] ?? "0", CultureInfo.InvariantCulture);
            var minDeposit = BigInteger.Parse(child["MinDepositBaseUnits"] ?? "0", CultureInfo.InvariantCulture);

            policies[chain] = new DepositPolicy(strategy, confirmations, minDeposit);
        }

        _policies = policies;
    }

    public DepositPolicy For(Chain chain) =>
        _policies.TryGetValue(chain, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No deposit policy configured for {chain}. Add 'Deposit:Policies:{chain}' with a CreditStrategy and Confirmations.");
}
