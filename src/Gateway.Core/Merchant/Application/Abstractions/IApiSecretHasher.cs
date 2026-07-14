namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

/// <summary>
/// Port for hashing API secrets. The implementation holds a server-side pepper, which is why this
/// lives behind an interface: the pepper is a secret and must never reach the Domain or the DB.
/// </summary>
public interface IApiSecretHasher
{
    /// <summary>Version of the pepper currently used for new hashes. Persisted alongside the hash.</summary>
    int CurrentVersion { get; }

    string Hash(string secret);

    /// <summary>
    /// Constant-time verification against the pepper identified by <paramref name="version"/>,
    /// so old credentials keep working across a pepper rotation.
    /// </summary>
    bool Verify(string secret, string secretHash, int version);
}
