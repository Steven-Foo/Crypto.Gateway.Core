namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

/// <summary>
/// Deliberately a different port from Merchant's <c>IApiSecretHasher</c>: that one hashes a
/// high-entropy, machine-generated secret (a fast keyed hash is sound there). A human-chosen password has
/// far less entropy, so this needs a slow, salted hash (PBKDF2) — same job, different threat model.
/// </summary>
public interface IStaffPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}
