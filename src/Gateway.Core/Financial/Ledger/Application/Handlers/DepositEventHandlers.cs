using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;

/// <summary>
/// Credits the ledger when a deposit confirms. A thin adapter: it parses the base-unit amount and
/// delegates to <see cref="ILedgerPoster"/>. Idempotency lives in the poster/DB, so a redelivered
/// event is harmless — <see cref="PostingOutcome.AlreadyPosted"/> is a success.
/// </summary>
public sealed class DepositConfirmedHandler(ILedgerPoster poster) : IIntegrationEventHandler<DepositConfirmed>
{
    public async Task HandleAsync(DepositConfirmed @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);

        var result = await poster.CreditDepositAsync(
            new CreditDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount), cancellationToken);

        // A business failure here (e.g. a malformed amount) is not a normal outcome for a confirmed
        // deposit — surface it so the outbox dispatcher retries / dead-letters rather than dropping money.
        if (result.IsFailure)
            throw new DomainException($"Ledger credit failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}

/// <summary>Posts the compensating journal when a confirmed deposit is orphaned by a reorg.</summary>
public sealed class DepositOrphanedHandler(ILedgerPoster poster) : IIntegrationEventHandler<DepositOrphaned>
{
    public async Task HandleAsync(DepositOrphaned @event, CancellationToken cancellationToken = default)
    {
        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);

        var result = await poster.ReverseDepositAsync(
            new ReverseDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, amount), cancellationToken);

        if (result.IsFailure)
            throw new DomainException($"Ledger reversal failed for deposit {@event.DepositId}: {result.Error!.Code} — {result.Error!.Message}");
    }
}
