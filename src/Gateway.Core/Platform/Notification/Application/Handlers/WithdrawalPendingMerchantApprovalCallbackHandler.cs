using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Tells the merchant that a payout one of their portal users submitted is waiting for their OWN approver —
/// the push that replaces "somebody has to remember to check the portal". Same envelope, signing scheme, and
/// schedule-don't-send split as the other withdrawal callbacks.
///
/// <para>Unlike the confirmed/failed callbacks this reports an <b>action still required</b>, not an outcome:
/// nothing has moved and nothing will until a human decides. The payload therefore carries the amount and
/// destination, which is what an approver needs to judge it, and deliberately NOT a link or token that could
/// approve it — approval happens in an authenticated portal session, never by following a webhook.</para>
/// </summary>
public sealed class WithdrawalPendingMerchantApprovalCallbackHandler(
    IMerchantCallbackSigner signer,
    ICallbackDeliveryScheduler scheduler,
    ILogger<WithdrawalPendingMerchantApprovalCallbackHandler> logger)
    : IIntegrationEventHandler<WithdrawalPendingMerchantApproval>
{
    private const string CallbackType = "crypto-transaction";

    public async Task HandleAsync(WithdrawalPendingMerchantApproval @event, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(@event.CallbackUrl))
            return; // the merchant did not ask for a callback

        var body = BuildPayload(@event);

        var signature = await signer.SignAsync(@event.MerchantId, body, cancellationToken);
        if (signature.IsFailure)
        {
            logger.LogWarning(
                "No signing credential for merchant {MerchantId}; payout approval-required callback skipped.",
                @event.MerchantId);
            return;
        }

        await scheduler.ScheduleAsync(
            CallbackReferenceType.Withdrawal, @event.WithdrawalId,
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex,
            cancellationToken);
    }

    private static string BuildPayload(WithdrawalPendingMerchantApproval e) =>
        JsonSerializer.Serialize(new
        {
            transactionId = e.MerchantTransactionId,
            data = new
            {
                transactionId = e.MerchantTransactionId,
                referenceNo = e.WithdrawalId,
                type = "withdraw",
                // A distinct status from the frozen pending/confirmed/failed vocabulary, so an existing
                // integration that switches on status simply ignores it rather than mistaking a payout that
                // needs a human for one already on its way.
                status = "pending_merchant_approval",
                amount = e.AmountBaseUnits,
                fee = e.FeeBaseUnits,
                receivingAddress = e.ReceivingAddress,
                timestamp = e.RequestedAt,
            },
        });
}
