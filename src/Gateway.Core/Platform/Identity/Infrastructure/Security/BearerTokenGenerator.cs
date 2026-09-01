using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>
/// Staff session tokens, from the shared <see cref="OpaqueToken"/> primitive: 256 bits of CSPRNG output, of
/// which only the SHA-256 hash is ever stored. The port and the issued-token record stay module-owned (§4.5).
/// </summary>
public sealed class BearerTokenGenerator : IBearerTokenGenerator
{
    public GeneratedBearerToken Generate()
    {
        var raw = OpaqueToken.Generate();
        return new GeneratedBearerToken(raw, HashOf(raw));
    }

    public string HashOf(string rawToken) => OpaqueToken.Sha256Hex(rawToken);
}
