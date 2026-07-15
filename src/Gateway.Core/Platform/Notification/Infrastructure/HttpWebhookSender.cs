using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure;

/// <summary>
/// Posts a signed callback over a short-timeout HTTP client. Returns whether the merchant accepted it (2xx);
/// a non-2xx or a transport error is a soft failure the caller turns into an outbox retry — never a throw
/// here, so one bad endpoint never crashes the dispatcher.
/// </summary>
public sealed class HttpWebhookSender(IHttpClientFactory httpClientFactory, ILogger<HttpWebhookSender> logger) : IWebhookSender
{
    public const string HttpClientName = "merchant-callback";

    public async Task<bool> SendAsync(
        string url, string body, string callbackType, string timestamp, string signatureHex, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Callback-Type", callbackType);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", signatureHex);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Merchant callback to {Url} returned {Status}.", url, (int)response.StatusCode);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Merchant callback to {Url} failed.", url);
            return false;
        }
    }
}
