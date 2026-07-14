using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

/// <summary>
/// A borrowed secret, wiped on <see cref="Dispose"/>.
///
/// Secrets are never modelled as <see cref="string"/>: .NET strings are immutable and cannot be
/// overwritten, so a mnemonic held in one lingers in the managed heap until a GC compaction
/// happens to reuse the memory — "zeroize immediately" is impossible for them. Hence <c>byte[]</c>
/// plus <see cref="CryptographicOperations.ZeroMemory"/>.
///
/// This is best-effort, not a guarantee: the GC may still have copied the buffer while moving it.
/// Real defence lives in the HSM/KMS boundary; this narrows the window.
/// </summary>
public sealed class SecretLease : IDisposable
{
    private byte[]? _buffer;

    public SecretLease(byte[] buffer) => _buffer = buffer;

    public ReadOnlySpan<byte> Value =>
        _buffer ?? throw new ObjectDisposedException(nameof(SecretLease));

    /// <summary>
    /// Only for material that is <b>not</b> secret — an account xpub, for example. The resulting
    /// string cannot be wiped, so never call this on a seed or private key.
    /// </summary>
    public string AsPublicUtf8String() => Encoding.UTF8.GetString(Value);

    public void Dispose()
    {
        if (_buffer is null)
            return;

        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = null;
    }
}

/// <summary>
/// Fetches material by reference from a KMS/HSM/vault. Swapping AWS Secrets Manager for Azure Key
/// Vault, HashiCorp Vault, or an HSM must require no change to business logic or schema.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Which provider this implementation serves; used to resolve per-HD-wallet.</summary>
    Domain.SecretProviderKind Kind { get; }

    Task<SecretLease> GetAsync(string reference, CancellationToken cancellationToken = default);
}

public interface ISecretProviderFactory
{
    bool Supports(Domain.SecretProviderKind kind);

    ISecretProvider For(Domain.SecretProviderKind kind);
}
