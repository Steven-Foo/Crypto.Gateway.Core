using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;

public static class CallbackDeliveryErrors
{
    public static readonly Error NotFound =
        Error.NotFound("callback_delivery.not_found", "No callback delivery record exists for this transaction.");

    public static readonly Error NotAbandoned =
        Error.Conflict("callback_delivery.not_abandoned", "A manual resend is only available once automatic retries have been abandoned.");
}
