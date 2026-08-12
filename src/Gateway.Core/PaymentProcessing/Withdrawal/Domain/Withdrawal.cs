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
        string? callbackUrl,
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
        CallbackUrl = callbackUrl;
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

    /// <summary>
    /// Which hot-pool wallet this payout is being sent FROM, stamped at sign time. A wallet is "busy" (leased)
    /// while a withdrawal carrying its id is in <see cref="WithdrawalStatus.Signing"/>/<see cref="WithdrawalStatus.Broadcast"/>
    /// — committed but not yet confirmed — so the pool allocator serializes each wallet to one in-flight
    /// transaction at a time. Null until signed. A filtered unique index enforces the one-in-flight rule.
    /// </summary>
    public Guid? SourceWalletId { get; private set; }

    /// <summary>Merchant's own callback endpoint for this withdrawal, carried on the confirmation/failure
    /// events so Notification never has to look it up (§4.5, mirrors PaymentIntent's own CallbackUrl).</summary>
    public string? CallbackUrl { get; private set; }

    /// <summary>
    /// The signed, broadcast-ready transaction blob, persisted the moment it is signed (see
    /// <see cref="RecordSigned"/>). Opaque bytes — public, broadcastable, never key material. Retained so a
    /// re-processing pass re-broadcasts the <em>same</em> transaction instead of building a new one.
    /// </summary>
    public byte[]? SignedTransaction { get; private set; }

    public bool HasSignedTransaction => SignedTransaction is { Length: > 0 };

    public string? TransactionHash { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Why the withdrawal is currently parked (<see cref="WithdrawalStatus.AwaitingFunds"/>/
    /// <see cref="WithdrawalStatus.AwaitingRelease"/>) — a human-readable trace for the ops screen ("needs
    /// 1,000, hot wallet holds 640"). Distinct from <see cref="FailureReason"/>: a hold is not a failure, so
    /// it must never read as one. Cleared when the withdrawal resumes.
    /// </summary>
    public string? StatusReason { get; private set; }

    /// <summary>The operator who released a large (above-threshold) parked withdrawal for sending, and when.
    /// Once set, the withdrawal is treated as auto-cleared on subsequent passes — a fund dip that re-parks it
    /// never demands a second release.</summary>
    public string? ReleasedBy { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>
    /// On-chain confirmation depth for the broadcast transaction, refreshed every confirmation-worker pass.
    /// Null until broadcast; a tracking/observability number only — <see cref="Confirm"/> (not this) is what
    /// actually settles the ledger, once <see cref="Status"/> crosses the policy's required depth.
    /// </summary>
    public int? Confirmations { get; private set; }

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
        string? callbackUrl,
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
            idempotencyKey.Trim(), string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl.Trim(),
            WithdrawalStatus.Reserving, now));
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

    /// <summary>
    /// Records the signed transaction blob and moves Approved → Signing in one step. Persisting the signed
    /// blob <em>before</em> broadcast is the money-out safety guarantee: a transaction's on-chain identity is
    /// fixed once signed, so a crash-and-retry re-broadcasts <b>this exact blob</b> (identical tx id, which the
    /// chain dedups) rather than building a fresh transaction the chain would treat as a second, distinct send
    /// — the double-send hazard on chains (like TRON) that stamp a fresh reference/expiry at build time.
    /// </summary>
    public Result RecordSigned(Guid signingRequestId, Guid sourceWalletId, byte[] signedTransaction, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Approved)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (signedTransaction is not { Length: > 0 } || sourceWalletId == Guid.Empty)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        SigningRequestId = signingRequestId;
        SourceWalletId = sourceWalletId; // leases the pool wallet until this withdrawal confirms or fails
        SignedTransaction = signedTransaction;
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

    /// <summary>
    /// Refreshes the observed confirmation depth. Only meaningful once broadcast; a no-op otherwise (the
    /// confirmation worker only ever calls this for <see cref="WithdrawalStatus.Broadcast"/> rows anyway, but
    /// the guard keeps a stray call from confusing a state it doesn't apply to).
    /// </summary>
    public Result RecordConfirmations(int confirmations, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.Broadcast)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Confirmations = confirmations < 0 ? 0 : confirmations;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Confirms a broadcast withdrawal → raises <see cref="WithdrawalConfirmed"/> (Ledger settles). The
    /// optional <paramref name="gasFeeSun"/>/<paramref name="gasAssetId"/> carry the native-coin fee the
    /// platform paid on-chain, so the Ledger can book it as a platform gas expense (5c); both default to
    /// "no gas" (fee 0, no asset) when the engine charged no fee or no gas asset is configured.
    /// </summary>
    public Result Confirm(DateTimeOffset now, BigInteger gasFeeSun = default, Guid? gasAssetId = null)
    {
        if (Status != WithdrawalStatus.Broadcast)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.Confirmed;
        UpdatedAt = now;
        Raise(new WithdrawalConfirmed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), TransactionHash!, now,
            IdempotencyKey, DestinationAddress, CallbackUrl,
            gasAssetId?.ToString(), ToBaseUnits(gasFeeSun < BigInteger.Zero ? BigInteger.Zero : gasFeeSun)));
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

    // ── Funding holds (physical hot-wallet float, independent of the ledger reserve) ──────────────────────
    // These transitions never raise an event: the ledger reserve stays exactly as placed at creation. Parking
    // a withdrawal because the hot wallet is short is a deferral, NOT a release — the merchant is still owed it.

    /// <summary>
    /// Parks a withdrawal whose hot wallet cannot physically cover it. Reserve held; the processing worker
    /// re-evaluates it every pass and resumes once the float recovers. Reachable from
    /// <see cref="WithdrawalStatus.Approved"/> or from either hold (a re-park that just refreshes the reason).
    /// </summary>
    public Result Park(string reason, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.Approved or WithdrawalStatus.AwaitingFunds or WithdrawalStatus.AwaitingRelease))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.AwaitingFunds;
        StatusReason = reason;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// The float is sufficient but the amount is above the approval threshold, so a human must release it to
    /// send (the "large = manual" resume rule). Reserve held; cleared by <see cref="ReleaseForSend"/>.
    /// </summary>
    public Result MarkAwaitingRelease(string reason, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.Approved or WithdrawalStatus.AwaitingFunds or WithdrawalStatus.AwaitingRelease))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.AwaitingRelease;
        StatusReason = reason;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Auto-resume of a parked withdrawal that is now clear to send (float sufficient AND either below the
    /// threshold or already released). Returns it to <see cref="WithdrawalStatus.Approved"/> so the normal
    /// build → sign → broadcast path runs. Internal to the processing worker — not an ops action.
    /// </summary>
    public Result ResumeToApproved(DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.AwaitingFunds or WithdrawalStatus.AwaitingRelease))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.Approved;
        StatusReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// An operator releases a large parked withdrawal for sending. Records who/when (so a later fund dip that
    /// re-parks it never demands a second release) and returns it to <see cref="WithdrawalStatus.Approved"/>;
    /// the worker sends it on the next pass once the float is sufficient.
    /// </summary>
    public Result ReleaseForSend(string releasedBy, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.AwaitingRelease or WithdrawalStatus.AwaitingFunds))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(releasedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        ReleasedBy = releasedBy.Trim();
        ReleasedAt = now;
        Status = WithdrawalStatus.Approved;
        StatusReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// An operator abandons a parked withdrawal that cannot be funded — the one path that releases the reserve
    /// from a hold. Only reachable from a hold (never once signed/broadcast, funds may be on-chain). Raises
    /// <see cref="WithdrawalFailed"/> so the ledger returns the reserved funds to the merchant.
    /// </summary>
    public Result Cancel(string cancelledBy, string reason, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.AwaitingFunds or WithdrawalStatus.AwaitingRelease))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        var detail = string.IsNullOrWhiteSpace(cancelledBy) ? reason : $"cancelled by {cancelledBy.Trim()}: {reason}";
        FailureReason = detail;
        StatusReason = null;
        Status = WithdrawalStatus.Failed;
        UpdatedAt = now;
        RaiseReleased(detail, now);
        return Result.Success();
    }

    private void RaiseReleased(string reason, DateTimeOffset now) =>
        Raise(new WithdrawalFailed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), reason, now,
            IdempotencyKey, CallbackUrl));

    private static string ToBaseUnits(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
