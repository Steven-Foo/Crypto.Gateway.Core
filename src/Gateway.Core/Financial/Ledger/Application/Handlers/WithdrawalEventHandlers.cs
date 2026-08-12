using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;

/// <summary>Settles the ledger when a withdrawal confirms on-chain: funds leave custody, fee → revenue, and
/// (5c) the native-coin gas the platform paid is booked as a platform expense. Settle + gas are separate,
/// idempotent journals; at-least-once redelivery re-runs both safely (§7.3).</summary>
public sealed class WithdrawalConfirmedHandler(ILedgerPoster poster) : IIntegrationEventHandler<WithdrawalConfirmed>
{
    public async Task HandleAsync(WithdrawalConfirmed @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = BigInteger.Parse(@event.FeeBaseUnits, CultureInfo.InvariantCulture);

        var result = await poster.SettleWithdrawalAsync(
            new SettleWithdrawalCommand(@event.WithdrawalId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger settle failed for withdrawal {@event.WithdrawalId}: {result.Error!.Code} — {result.Error!.Message}");

        // 5c: book the platform gas cost if the confirmation carried a gas asset (a zero fee is a no-op).
        if (Guid.TryParse(@event.GasAssetId, out var gasAssetId))
        {
            var gasFee = BigInteger.Parse(@event.GasFeeBaseUnits, CultureInfo.InvariantCulture);
            var gas = await poster.RecordGasSpentAsync(
                new RecordGasSpentCommand(@event.WithdrawalId, "Withdrawal", gasAssetId, gasFee), cancellationToken);

            if (gas.IsFailure)
                throw new DomainException($"Ledger gas booking failed for withdrawal {@event.WithdrawalId}: {gas.Error!.Code} — {gas.Error!.Message}");
        }
    }
}

/// <summary>Releases the reserved funds back to the merchant when a withdrawal is rejected or fails pre-broadcast.</summary>
public sealed class WithdrawalFailedHandler(ILedgerPoster poster) : IIntegrationEventHandler<WithdrawalFailed>
{
    public async Task HandleAsync(WithdrawalFailed @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = BigInteger.Parse(@event.FeeBaseUnits, CultureInfo.InvariantCulture);

        var result = await poster.ReleaseWithdrawalAsync(
            new ReleaseWithdrawalCommand(@event.WithdrawalId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger release failed for withdrawal {@event.WithdrawalId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}
