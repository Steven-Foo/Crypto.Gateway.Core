using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;

/// <summary>One-time passwords from the shared <see cref="TemporaryPassword"/> primitive, so the staff and
/// merchant-portal modules cannot drift on entropy. The port stays module-owned (§4.5).</summary>
public sealed class MerchantPasswordGenerator : IMerchantPasswordGenerator
{
    public string Generate() => TemporaryPassword.Generate();
}
