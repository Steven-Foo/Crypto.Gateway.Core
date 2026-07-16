using System.Collections.Concurrent;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. A thread-safe, <em>writable</em> in-memory secret store: pre-seeded from
/// configuration, and written to at runtime by the dev provisioner when it mints a per-merchant wallet's
/// account xpub. It supersedes the immutable <c>InMemorySecretProvider</c> for the per-merchant flow, where
/// new public keys appear after startup.
///
/// It reports <see cref="SecretProviderKind.InMemoryDevelopment"/>, so a production HD wallet can never
/// resolve to it — registering this in production is a §10 violation. It holds public xpub material only;
/// no seed or private key is ever written here (the dev provisioner stores neither).
/// </summary>
public sealed class MutableInMemorySecretStore : ISecretProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets;

    public MutableInMemorySecretStore(IReadOnlyDictionary<string, string>? seed = null) =>
        _secrets = new ConcurrentDictionary<string, byte[]>(
            (seed ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => Encoding.UTF8.GetBytes(p.Value)));

    public SecretProviderKind Kind => SecretProviderKind.InMemoryDevelopment;

    /// <summary>Stores (or overwrites) a value. Idempotent for a deterministic reference/value pair.</summary>
    public void Put(string reference, string value) => _secrets[reference] = Encoding.UTF8.GetBytes(value);

    public Task<SecretLease> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!_secrets.TryGetValue(reference, out var value))
            throw new KeyNotFoundException($"No secret is registered for reference '{reference}'.");

        // A defensive copy: the lease zeroes what it is given on Dispose, and we must not wipe our own store.
        return Task.FromResult(new SecretLease(value.ToArray()));
    }
}
