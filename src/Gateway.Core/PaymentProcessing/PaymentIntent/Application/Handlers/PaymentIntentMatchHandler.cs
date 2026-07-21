using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Handlers;

/// <summary>
/// Matches a confirmed deposit to the live invoice holding its address. This is a merchant-facing overlay,
/// not the money path — the Ledger credits the merchant independently, so this handler never fails a credit.
///
/// Idempotent two ways: a deposit already matched to some intent is skipped (closing the address-reuse
/// redelivery hole — an old deposit must not resolve a newer invoice on the same reused address), and a
/// redelivered event whose intent is already Matched finds no Waiting intent and no-ops.
/// </summary>
public sealed class PaymentIntentMatchHandler(
    IPaymentIntentRepository repository,
    IWalletReservationLock walletLock,
    TimeProvider timeProvider,
    ILogger<PaymentIntentMatchHandler> logger) : IIntegrationEventHandler<DepositConfirmed>
{
    public async Task HandleAsync(DepositConfirmed @event, CancellationToken cancellationToken = default)
    {
        if (await repository.IsDepositMatchedAsync(@event.DepositId, cancellationToken))
            return; // already matched this exact deposit — nothing to do

        var intent = await repository.FindWaitingByWalletAsync(@event.WalletId, cancellationToken);
        if (intent is null)
            return; // no live invoice for this address (direct/late deposit) — ledger still credited it

        var amount = BigInteger.Parse(@event.AmountBaseUnits, CultureInfo.InvariantCulture);
        intent.MatchTo(@event.DepositId, @event.TransactionHash, amount, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        // Resolved — free the wallet immediately rather than waiting out the reservation's TTL, so the next
        // invoice can reuse it right away.
        await walletLock.ReleaseAsync(intent.WalletId, cancellationToken);

        logger.LogInformation(
            "Payment intent {Reference} matched to deposit {DepositId} (amount matched: {Matched}).",
            intent.PublicReference, @event.DepositId, intent.AmountMatched);
    }
}
