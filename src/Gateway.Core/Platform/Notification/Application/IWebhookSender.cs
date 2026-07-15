namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;

/// <summary>
/// Port for the outbound HTTP POST of a signed callback. Behind an interface so the notification use-case
/// stays free of infrastructure (§4.4): the handler builds and signs the body; this delivers it and reports
/// whether the merchant accepted it (2xx).
/// </summary>
public interface IWebhookSender
{
    Task<bool> SendAsync(
        string url,
        string body,
        string callbackType,
        string timestamp,
        string signatureHex,
        CancellationToken cancellationToken = default);
}
