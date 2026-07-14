using System.Text;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;

/// <summary>
/// A development/test stand-in for the signer. It produces a deterministic "signed" blob and, crucially,
/// <b>never touches key material</b> — no seed, no derivation, no KMS. A production signer replaces this
/// behind the same <see cref="ISigner"/> port: load the seed as a zeroized SecretLease, derive the child
/// key in memory, sign, dispose, audit (§10). This fake exists so the withdrawal orchestration is
/// testable without a real key or HSM.
/// </summary>
public sealed class InMemorySigner : ISigner
{
    public Task<Result<SignedTransaction>> SignAsync(SigningRequest request, CancellationToken cancellationToken = default)
    {
        var signed = new byte[request.UnsignedPayload.Length + 0];
        request.UnsignedPayload.CopyTo(signed, 0);

        // Append a deterministic marker so the blob differs from the unsigned one (no real signature here).
        var marker = Encoding.UTF8.GetBytes($":signed:{request.Chain}:{request.KeyReference}");
        var result = new byte[signed.Length + marker.Length];
        signed.CopyTo(result, 0);
        marker.CopyTo(result, signed.Length);

        return Task.FromResult(Result.Success(new SignedTransaction(result)));
    }
}
