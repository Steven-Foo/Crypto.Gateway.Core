using System.Text;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON node error messages come back hex-encoded ASCII (e.g. <c>"636f6e7472616374…"</c>). Decodes them
/// to readable text for logging/ProblemDetails; passes plain text through unchanged.
/// </summary>
public static class TronErrorMessage
{
    public static string? Decode(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        // Only decode when the whole string is valid, even-length hex — otherwise it's already plain text.
        if (message.Length % 2 != 0 || !message.All(Uri.IsHexDigit))
            return message;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(message));
        }
        catch (FormatException)
        {
            return message;
        }
    }
}
