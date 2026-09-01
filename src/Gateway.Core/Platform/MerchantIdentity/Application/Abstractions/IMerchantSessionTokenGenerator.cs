namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

/// <summary>The raw token is shown to the caller once, at issue time, and never stored — only <see cref="Hash"/> is.</summary>
public sealed record GeneratedSessionToken(string RawToken, string Hash);

public interface IMerchantSessionTokenGenerator
{
    GeneratedSessionToken Generate();

    /// <summary>Hashes a presented token the same way, for lookup against the stored <see cref="GeneratedSessionToken.Hash"/>.</summary>
    string HashOf(string rawToken);
}
