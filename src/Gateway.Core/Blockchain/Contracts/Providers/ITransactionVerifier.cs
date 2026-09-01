using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>What the caller expected a recorded transaction to have done.</summary>
/// <param name="Chain">Chain the transaction was broadcast on.</param>
/// <param name="TransactionHash">The hash the operator recorded.</param>
/// <param name="ExpectedTo">The address the funds must have arrived at.</param>
/// <param name="AssetId">The asset that must have moved.</param>
/// <param name="MinimumAmount">
/// The least the transfer may be, in base units. A larger transfer passes: an admin may round up or batch,
/// and paying a merchant more than owed is an operational matter, not a correctness failure — whereas paying
/// less would silently discharge an obligation that was not met.
/// </param>
public sealed record VerifyTransferRequest(
    Chain Chain, string TransactionHash, string ExpectedTo, Guid AssetId, BigInteger MinimumAmount);

/// <summary>What the chain actually shows, once a transfer has been verified.</summary>
public sealed record VerifiedTransfer(string TransactionHash, string To, BigInteger Amount, long BlockNumber);

/// <summary>
/// Confirms that a transaction an operator says they made really happened, and really did what they claim.
///
/// <para><b>Read-only and keyless (§10)</b> — it observes the chain and never builds, signs, or broadcasts.
/// It exists because parts of this system now record payments made OUTSIDE platform custody: an admin tops up
/// a hot wallet, or pays a merchant settlement, from a company wallet, and then types the hash into the UI.
/// Without verification, "completed" would be an unverified human claim, and a typo or a mispasted hash would
/// discharge a merchant's reserved balance against a transfer that never happened.</para>
///
/// <para>The check is deliberately narrow: the transaction exists, is confirmed, and moved at least the
/// expected amount of the expected asset to the expected address. It does NOT check the sender — the admin
/// pays from whichever company wallet suits them, and constraining that would block legitimate operations
/// while adding nothing (a wrong sender still cannot fake a correct destination and amount).</para>
/// </summary>
public interface ITransactionVerifier
{
    Task<Result<VerifiedTransfer>> VerifyAsync(VerifyTransferRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Distinct failure codes, so an operator can tell WHY a hash was refused and fix the right thing — "still
/// broadcasting, wait" is a completely different action from "wrong hash" or "wrong amount". Collapsing these
/// into one generic error would leave them guessing at exactly the moment money is involved.
/// </summary>
public static class TransactionVerificationErrors
{
    public static readonly Error NotFound = Error.Validation(
        "verification.tx_not_found",
        "No transaction with that hash was found on chain. Check the hash, or wait if it was only just broadcast.");

    public static readonly Error NotConfirmed = Error.Validation(
        "verification.tx_not_confirmed",
        "The transaction has been broadcast but is not yet confirmed. Wait for confirmation and record it again.");

    public static readonly Error Failed = Error.Validation(
        "verification.tx_failed",
        "The transaction is on chain but failed to execute, so no funds moved.");

    public static readonly Error DestinationMismatch = Error.Validation(
        "verification.destination_mismatch",
        "The transaction did not send funds to the expected address.");

    public static readonly Error AssetMismatch = Error.Validation(
        "verification.asset_mismatch",
        "The transaction moved a different asset than expected.");

    public static readonly Error AmountMismatch = Error.Validation(
        "verification.amount_mismatch",
        "The transaction moved less than the expected amount.");

    public static readonly Error Unsupported = Error.Validation(
        "verification.unsupported_chain",
        "Transaction verification is not available for this chain.");
}
