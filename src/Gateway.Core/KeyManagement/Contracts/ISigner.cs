using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;

/// <summary>An unsigned transaction to sign, plus which key signs it. The payload is opaque bytes to the caller.</summary>
public sealed record SigningRequest(Guid WithdrawalId, Chain Chain, byte[] UnsignedPayload, string KeyReference);

/// <summary>The signed transaction blob, ready to broadcast. Never contains key material.</summary>
public sealed record SignedTransaction(byte[] SignedPayload);

/// <summary>
/// The signing boundary (§10). Callers hand over an <em>unsigned</em> blob and a key <em>reference</em>
/// and get back a <em>signed</em> blob — the private key never crosses this interface. The
/// implementation loads the seed as a zeroized <c>SecretLease</c> (byte[]), derives the child key in
/// memory, signs, disposes, and writes a signing-audit record. Keys are never returned, logged, or
/// persisted. The application only ever holds unsigned/signed blobs.
/// </summary>
public interface ISigner
{
    Task<Result<SignedTransaction>> SignAsync(SigningRequest request, CancellationToken cancellationToken = default);
}
