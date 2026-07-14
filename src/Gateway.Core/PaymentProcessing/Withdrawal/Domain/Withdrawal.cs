using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

/// <summary>
/// A merchant's request to move asset A to an external address — the money-out aggregate. It owns the
/// lifecycle state machine and raises the events the Ledger settles/releases against; it never touches
/// keys (signing lives behind a port) and never posts to the ledger directly (§4.5, §10).
///
/// Money-safety rules in the transitions:
/// <list type="bullet">
///   <item>funds are reserved in the ledger before this is created, so a withdrawal only exists for money already locked;</item>
///   <item>a release (Reject/Fail) is only reachable <em>before</em> broadcast — once funds may be on-chain, only Confirm or an ops incident;</item>
///   <item>every transition is guarded, so an out-of-order worker call is refused, not silently applied.</item>
/// </list>
/// </summary>
public sealed class Withdrawal : Entity<Guid>
{
    private Withdrawal(
        Guid id,
        Guid merchantId,
        Guid assetId,
        Chain chain,
        string destinationAddress,
        BigInteger amount,
        BigInteger fee,
        string idempotencyKey,
        WithdrawalStatus status,
        DateTimeOffset now) : base(id)
    {
        MerchantId = merchantId;
        AssetId = assetId;
        Chain = chain;
        DestinationAddress = destinationAddress;
        Amount = amount;
        Fee = fee;
        IdempotencyKey = idempotencyKey;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Withdrawal() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public Guid AssetId { get; private set; }
    public Chain Chain { get; private set; }
    public string DestinationAddress { get; private set; } = null!;
    public BigInteger Amount { get; private set; }
    public BigInteger Fee { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public WithdrawalStatus Status { get; private set; }
    public string? ApprovedBy { get; private set; }
    public Guid? SigningRequestId { get; private set; }
    public string? TransactionHash { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a withdrawal in <see cref="WithdrawalStatus.Reserving"/>. The caller then reserves the
    /// funds in the ledger and calls <see cref="ConfirmReserved"/> (success) or
    /// <see cref="MarkReserveFailed"/> (insufficient). Creating first, reserving second — deduped by the
    /// idempotency key — means funds are never double-debited nor left reserved without a record.
    /// <paramref name="fee"/> is the on-top platform fee.
    /// </summary>
    public static Result<Withdrawal> Request(
        Guid merchantId,
        Guid assetId,
        Chain chain,
        string destinationAddress,
        BigInteger amount,
        BigInteger fee,
        string idempotencyKey,
        DateTimeOffset now)
    {
        if (merchantId == Guid.Empty || assetId == Guid.Empty)
            return Result.Failure<Withdrawal>(WithdrawalErrors.OwnerRequired);

        if (string.IsNullOrWhiteSpace(destinationAddress))
            return Result.Failure<Withdrawal>(WithdrawalErrors.DestinationRequired);

        if (amount <= BigInteger.Zero || fee < BigInteger.Zero)
            return Result.Failure<Withdrawal>(WithdrawalErrors.AmountNotPositive);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<Withdrawal>(WithdrawalErrors.IdempotencyKeyRequired);

        return Result.Success(new Withdrawal(
            Guid.CreateVersion7(), merchantId, assetId, chain, destinationAddress.Trim(), amount, fee,
            idempotencyKey.Trim(), WithdrawalStatus.Reserving, now));
    }

    /// <summary>Funds are locked. Moves to PendingApproval above the threshold, otherwise Approved.</summary>
    public Result ConfirmReserved(bool requiresApproval, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Reserving)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = requiresApproval ? WithdrawalStatus.PendingApproval : WithdrawalStatus.Approved;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// The ledger reserve was refused (insufficient balance). Terminal-fails the withdrawal WITHOUT a
    /// release event — nothing was ever reserved, so there is nothing to return.
    /// </summary>
    public Result MarkReserveFailed(string reason, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Reserving)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        FailureReason = reason;
        Status = WithdrawalStatus.Failed;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Approve(string approvedBy, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingApproval)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        ApprovedBy = approvedBy;
        Status = WithdrawalStatus.Approved;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Reject(string approvedBy, string reason, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingApproval)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        ApprovedBy = approvedBy;
        FailureReason = reason;
        Status = WithdrawalStatus.Rejected;
        UpdatedAt = now;
        RaiseReleased(reason, now); // return the reserved funds
        return Result.Success();
    }

    public Result BeginSigning(Guid signingRequestId, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Approved)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        SigningRequestId = signingRequestId;
        Status = WithdrawalStatus.Signing;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result MarkBroadcast(string transactionHash, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Signing)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        TransactionHash = transactionHash.Trim();
        Status = WithdrawalStatus.Broadcast;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Confirm(DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Broadcast)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.Confirmed;
        UpdatedAt = now;
        Raise(new WithdrawalConfirmed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), TransactionHash!, now));
        return Result.Success();
    }

    /// <summary>
    /// Fails a withdrawal that has not yet been broadcast (Approved/Signing) — safe to release, because
    /// nothing left the chain. Refused once Broadcast: funds may be on-chain, so that is an ops incident.
    /// </summary>
    public Result Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.Approved or WithdrawalStatus.Signing))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        FailureReason = reason;
        Status = WithdrawalStatus.Failed;
        UpdatedAt = now;
        RaiseReleased(reason, now);
        return Result.Success();
    }

    private void RaiseReleased(string reason, DateTimeOffset now) =>
        Raise(new WithdrawalFailed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), reason, now));

    private static string ToBaseUnits(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
