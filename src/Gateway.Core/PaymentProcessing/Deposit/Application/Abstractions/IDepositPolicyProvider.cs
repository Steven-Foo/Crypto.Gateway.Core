using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;

/// <summary>
/// Supplies the per-chain <see cref="DepositPolicy"/> (confirmations / finality / dust floor). Backed by
/// configuration in Infrastructure, where the human-readable minimum is converted to base units once.
/// </summary>
public interface IDepositPolicyProvider
{
    DepositPolicy For(Chain chain);
}
