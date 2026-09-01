using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;

/// <summary>
/// Merchant-portal passwords, hashed with the shared PBKDF2-SHA256 primitive
/// (<see cref="Pbkdf2PasswordHash"/>) — the same algorithm the staff module uses, from one implementation. The
/// port stays module-owned (§4.5): sharing the primitive does NOT couple the two identity models.
/// </summary>
public sealed class MerchantPasswordHasher : IMerchantPasswordHasher
{
    public string Hash(string password) => Pbkdf2PasswordHash.Hash(password);

    public bool Verify(string password, string hash) => Pbkdf2PasswordHash.Verify(password, hash);
}
