using System.Text;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. Holds material in process memory.
///
/// It reports <see cref="SecretProviderKind.InMemoryDevelopment"/>, so an <c>HdWallet</c> configured
/// for AWS/Azure/Vault/HSM will never resolve to it — a production wallet cannot silently fall back
/// to an in-memory seed. Registering this in production is a §10 violation.
/// </summary>
public sealed class InMemorySecretProvider(IReadOnlyDictionary<string, byte[]> secrets) : ISecretProvider
{
    public SecretProviderKind Kind => SecretProviderKind.InMemoryDevelopment;

    public static InMemorySecretProvider FromStrings(IReadOnlyDictionary<string, string> secrets) =>
        new(secrets.ToDictionary(p => p.Key, p => Encoding.UTF8.GetBytes(p.Value)));

    public Task<SecretLease> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!secrets.TryGetValue(reference, out var value))
            throw new KeyNotFoundException($"No secret is registered for reference '{reference}'.");

        // A defensive copy: the lease zeroes what it is given on Dispose, and we must not wipe the
        // provider's own backing store.
        return Task.FromResult(new SecretLease(value.ToArray()));
    }
}

public sealed class SecretProviderFactory : ISecretProviderFactory
{
    private readonly Dictionary<SecretProviderKind, ISecretProvider> _providers;

    public SecretProviderFactory(IEnumerable<ISecretProvider> providers) =>
        _providers = providers.ToDictionary(p => p.Kind);

    public bool Supports(SecretProviderKind kind) => _providers.ContainsKey(kind);

    public ISecretProvider For(SecretProviderKind kind) =>
        _providers.TryGetValue(kind, out var provider)
            ? provider
            : throw new InvalidOperationException($"No secret provider is registered for {kind}.");
}
