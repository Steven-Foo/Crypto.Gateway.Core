namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

/// <summary>Slow, salted password hashing for a human-chosen merchant-portal password (PBKDF2) — same threat
/// model as <c>Platform.Identity.IStaffPasswordHasher</c>, kept as its own port so the two identity modules
/// stay independent (§4.5).</summary>
public interface IMerchantPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}
