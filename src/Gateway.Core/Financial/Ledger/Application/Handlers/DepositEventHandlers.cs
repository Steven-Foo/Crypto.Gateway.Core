using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;

/// <summary>
/// Credits the ledger when a deposit confirms, splitting the platform deposit fee off the top (payer-on-top
/// pricing): the merchant is credited the net, the platform earns the fee. The fee is <b>not</b> derived
/// here — the Deposit module snapshotted it at detection and carries it on the event, so the Ledger books
/// exactly the fee the deposit was priced at and stays chain/pricing-agnostic (§4.6). Idempotency lives in
/// the poster/DB, so a redelivered event is harmless.
/// </summary>
public sealed class DepositConfirmedHandler(ILedgerPoster poster)
    : IIntegrationEventHandler<DepositConfirmed>
{
    public async Task HandleAsync(DepositConfirmed @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = DepositFee.Parse(@event.FeeBaseUnits);

        var result = await poster.CreditDepositAsync(
            new CreditDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        // A business failure here (e.g. a malformed amount) is not a normal outcome for a confirmed
        // deposit — surface it so the outbox dispatcher retries / dead-letters rather than dropping money.
        if (result.IsFailure)
            throw new DomainException($"Ledger credit failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}

/// <summary>
/// Posts the compensating journal when a confirmed deposit is orphaned by a reorg. It reverses the
/// <em>exact</em> fee that was credited — the deposit's snapshotted fee, carried on the event — so the
/// compensation mirrors the original split precisely. (This closes the former drift gap where the fee was
/// re-derived from the current schedule: the fee now travels with the deposit, Withdrawal-symmetric.)
/// </summary>
public sealed class DepositOrphanedHandler(ILedgerPoster poster)
    : IIntegrationEventHandler<DepositOrphaned>
{
    public async Task HandleAsync(DepositOrphaned @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        var fee = DepositFee.Parse(@event.FeeBaseUnits);

        var result = await poster.ReverseDepositAsync(
            new ReverseDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount, fee), cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger reversal failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}

/// <summary>Parses the fee carried on a deposit event. A pre-fee event (in-flight before the field existed)
/// carries null/empty ⇒ zero, collapsing to the original no-fee journal.</summary>
internal static class DepositFee
{
    public static BigInteger Parse(string? feeBaseUnits) =>
        string.IsNullOrEmpty(feeBaseUnits)
            ? BigInteger.Zero
            : BigInteger.Parse(feeBaseUnits, CultureInfo.InvariantCulture);
}
