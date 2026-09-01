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

        // The event says whether OUR custody paid this. A finance settlement was paid from a company wallet
        // outside platform custody, so it must not decrement TreasuryAsset — the Ledger stays ignorant of who
        // paid and simply honours the flag, keeping it free of any chain/custody knowledge (§4.6).
        var result = await poster.SettleWithdrawalAsync(
            new SettleWithdrawalCommand(
                @event.WithdrawalId, @event.MerchantId, @event.AssetId, amount, fee, @event.ExternallySettled),
            cancellationToken);

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

/// <summary>
/// Books company funds moved into a hot withdrawal wallet: DEBIT TreasuryAsset / CREDIT WithdrawalWalletTopUp.
///
/// <para>Custody genuinely rose — we hold more crypto in an address reconciliation watches — so TreasuryAsset
/// must move, or reconciliation would report drift equal to every top-up ever made. The credit is a
/// System-owned contribution account, so operating float can never be mistaken for merchant money and the
/// merchant-withdrawable formula is untouched (§14).</para>
/// </summary>
public sealed class HotWalletToppedUpHandler(ILedgerPoster poster) : IIntegrationEventHandler<HotWalletToppedUp>
{
    public async Task HandleAsync(HotWalletToppedUp @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);

        var result = await poster.RecordTopUpAsync(
            new RecordTopUpCommand(@event.TopUpId, @event.AssetId, amount, $"Hot wallet top-up {@event.TargetAddress}"),
            cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger top-up posting failed for {@event.TopUpId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}
