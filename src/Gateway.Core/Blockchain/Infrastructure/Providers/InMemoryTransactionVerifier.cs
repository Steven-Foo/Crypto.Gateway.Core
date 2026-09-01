using System.Collections.Concurrent;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// Dev/test stand-in for <see cref="ITransactionVerifier"/>, at the same §8 DI seam the real TRON verifier
/// occupies. A test or a developer declares what the chain "shows" via <see cref="Record"/>; anything not
/// declared verifies as not found.
///
/// <para><b>It refuses by default, deliberately.</b> A permissive fake would let the whole settlement-recording
/// flow pass in dev while the real verification path was never exercised — and the failure would first appear
/// in production, on a money path. Making the happy case require an explicit setup keeps the check honest.</para>
/// </summary>
public sealed class InMemoryTransactionVerifier : ITransactionVerifier
{
    private readonly ConcurrentDictionary<string, VerifiedTransfer> _transfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Error> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declares that this hash moved <paramref name="amount"/> to <paramref name="to"/> and is confirmed.</summary>
    public void Record(string transactionHash, string to, BigInteger amount, long blockNumber = 1) =>
        _transfers[transactionHash.Trim()] = new VerifiedTransfer(transactionHash.Trim(), to, amount, blockNumber);

    /// <summary>Declares that this hash fails verification for a specific reason (e.g. still unconfirmed).</summary>
    public void RecordFailure(string transactionHash, Error error) => _failures[transactionHash.Trim()] = error;

    public Task<Result<VerifiedTransfer>> VerifyAsync(
        VerifyTransferRequest request, CancellationToken cancellationToken = default)
    {
        var hash = request.TransactionHash.Trim();

        if (_failures.TryGetValue(hash, out var failure))
            return Task.FromResult(Result.Failure<VerifiedTransfer>(failure));

        if (!_transfers.TryGetValue(hash, out var transfer))
            return Task.FromResult(Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.NotFound));

        // The same checks the real verifier applies, so dev exercises the same rejections.
        if (!string.Equals(transfer.To, request.ExpectedTo.Trim(), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.DestinationMismatch));

        if (transfer.Amount < request.MinimumAmount)
            return Task.FromResult(Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.AmountMismatch));

        return Task.FromResult(Result.Success(transfer));
    }
}
