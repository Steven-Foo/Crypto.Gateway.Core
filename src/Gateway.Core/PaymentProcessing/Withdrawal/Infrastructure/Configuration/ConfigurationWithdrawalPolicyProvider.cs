using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Configuration;

/// <summary>
/// Builds each chain's <see cref="WithdrawalPolicy"/> from config <c>Withdrawal:Policies</c>. All amounts
/// are exact base-unit integers (never display values — §14). A missing policy throws on lookup: the
/// system must never process a withdrawal against unconfigured limits/fee/approval threshold.
/// A per-asset (rather than per-chain) policy is a future refinement.
/// </summary>
public sealed class ConfigurationWithdrawalPolicyProvider : IWithdrawalPolicyProvider
{
    private readonly IReadOnlyDictionary<Chain, WithdrawalPolicy> _policies;

    public ConfigurationWithdrawalPolicyProvider(IConfiguration configuration)
    {
        var policies = new Dictionary<Chain, WithdrawalPolicy>();

        foreach (var child in configuration.GetSection("Withdrawal:Policies").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            var minimum = BigInteger.Parse(child["MinimumBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
            var maximum = string.IsNullOrWhiteSpace(child["MaximumBaseUnits"])
                ? (BigInteger?)null
                : BigInteger.Parse(child["MaximumBaseUnits"]!, CultureInfo.InvariantCulture);
            var fee = BigInteger.Parse(child["FeeBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
            var approvalThreshold = BigInteger.Parse(child["ApprovalThresholdBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
            var confirmations = int.Parse(child["Confirmations"] ?? "0", CultureInfo.InvariantCulture);

            policies[chain] = new WithdrawalPolicy(minimum, maximum, fee, approvalThreshold, confirmations);
        }

        _policies = policies;
    }

    public WithdrawalPolicy For(Chain chain) =>
        _policies.TryGetValue(chain, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No withdrawal policy configured for {chain}. Add 'Withdrawal:Policies:{chain}'.");
}
