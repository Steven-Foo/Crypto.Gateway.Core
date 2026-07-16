using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;

/// <summary>
/// Credits the ledger when a deposit confirms, splitting the platform deposit fee off the top (payer-on-top
/// pricing): the merchant is credited the net, the platform earns the fee. The fee is resolved from the
/// merchant's schedule against the confirmed amount, so it needs no invoice — an intent-less deposit is
/// still priced. Idempotency lives in the poster/DB, so a redelivered event is harmless.
/// </summary>
public sealed class DepositConfirmedHandler(ILedgerPoster poster, IMerchantFeeSchedule feeSchedule)
    : IIntegrationEventHandler<DepositConfirmed>
{
    public async Task HandleAsync(DepositConfirmed @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = await feeSchedule.QuoteDepositFeeAsync(@event.MerchantId, @event.AssetId, amount, cancellationToken);

        var result = await poster.CreditDepositAsync(
            new CreditDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        // A business failure here (e.g. a malformed amount) is not a normal outcome for a confirmed
        // deposit — surface it so the outbox dispatcher retries / dead-letters rather than dropping money.
        if (result.IsFailure)
            throw new DomainException($"Ledger credit failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}

/// <summary>
/// Posts the compensating journal when a confirmed deposit is orphaned by a reorg. Re-derives the fee from
/// the same confirmed amount so the reversal mirrors the original split exactly.
///
/// NOTE (documented follow-up): the fee is recomputed from the current schedule rather than the one in
/// force at credit time. In the near-impossible case that a merchant's fee schedule changes during a
/// deposit's reorg window, the compensation would use the new rate. Exact-rate hardening would carry the
/// fee on the deposit event (the Withdrawal-symmetric approach) — deferred as it moves no realistic money.
/// </summary>
public sealed class DepositOrphanedHandler(ILedgerPoster poster, IMerchantFeeSchedule feeSchedule)
    : IIntegrationEventHandler<DepositOrphaned>
{
    public async Task HandleAsync(DepositOrphaned @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = await feeSchedule.QuoteDepositFeeAsync(@event.MerchantId, @event.AssetId, amount, cancellationToken);

        var result = await poster.ReverseDepositAsync(
            new ReverseDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger reversal failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}
