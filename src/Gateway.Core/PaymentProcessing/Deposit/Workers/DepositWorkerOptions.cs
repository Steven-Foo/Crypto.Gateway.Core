using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;

/// <summary>Which chains the deposit workers poll, and how often. Supplied by the host.</summary>
public sealed class DepositWorkerOptions
{
    public IReadOnlyList<Chain> Chains { get; init; } = [];

    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConfirmationInterval { get; init; } = TimeSpan.FromSeconds(10);
}
