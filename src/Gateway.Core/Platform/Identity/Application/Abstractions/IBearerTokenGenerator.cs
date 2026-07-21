namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

/// <summary>The raw token is shown to the caller once, at issue time, and never stored — only <see cref="Hash"/> is.</summary>
public sealed record GeneratedBearerToken(string RawToken, string Hash);

public interface IBearerTokenGenerator
{
    GeneratedBearerToken Generate();

    /// <summary>Hashes a presented token the same way, for lookup against the stored <see cref="GeneratedBearerToken.Hash"/>.</summary>
    string HashOf(string rawToken);
}
