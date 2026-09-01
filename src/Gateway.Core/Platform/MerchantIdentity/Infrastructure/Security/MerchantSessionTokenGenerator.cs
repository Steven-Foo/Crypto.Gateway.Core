using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;

/// <summary>
/// Merchant-portal session (and anti-CSRF) tokens, from the shared <see cref="OpaqueToken"/> primitive: 256
/// bits of CSPRNG output, of which only the SHA-256 hash is ever stored. The port and the issued-token record
/// stay module-owned (§4.5).
/// </summary>
public sealed class MerchantSessionTokenGenerator : IMerchantSessionTokenGenerator
{
    public GeneratedSessionToken Generate()
    {
        var raw = OpaqueToken.Generate();
        return new GeneratedSessionToken(raw, HashOf(raw));
    }

    public string HashOf(string rawToken) => OpaqueToken.Sha256Hex(rawToken);
}
