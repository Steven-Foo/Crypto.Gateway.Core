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
        WithdrawalKind kind,
        string destinationAddress,
        BigInteger amount,
        BigInteger fee,
        string merchantTransactionId,
        string? callbackUrl,
        WithdrawalStatus status,
        DateTimeOffset now,
        int feeBps,
        BigInteger feeFixed,
        BigInteger feeMinimum,
        bool minimumFeeApplied) : base(id)
    {
        MerchantId = merchantId;
        AssetId = assetId;
        Chain = chain;
        Kind = kind;
        DestinationAddress = destinationAddress;
        Amount = amount;
        Fee = fee;
        FeeBps = feeBps;
        FeeFixed = feeFixed;
        FeeMinimum = feeMinimum;
        MinimumFeeApplied = minimumFeeApplied;
        MerchantTransactionId = merchantTransactionId;
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

    /// <summary>User payout vs merchant earnings cash-out — see <see cref="WithdrawalKind"/>. The execution
    /// pipeline and ledger impact are identical; this only separates the request controls and reporting.</summary>
    public WithdrawalKind Kind { get; private set; }

    public string DestinationAddress { get; private set; } = null!;
    public BigInteger Amount { get; private set; }
    public BigInteger Fee { get; private set; }

    /// <summary>
    /// The merchant's withdrawal rate inputs at the moment this payout was priced — captured alongside
    /// <see cref="Fee"/> so the exact calculation is provable later even if the merchant's rate has since
    /// changed (fee transparency, matches 最低手续费). Zero/false for any withdrawal predating this feature.
    /// </summary>
    public int FeeBps { get; private set; }

    public BigInteger FeeFixed { get; private set; }

    public BigInteger FeeMinimum { get; private set; }

    /// <summary>True when the 最低手续费 floor actually determined <see cref="Fee"/>.</summary>
    public bool MinimumFeeApplied { get; private set; }

    public string MerchantTransactionId { get; private set; } = null!;
    public WithdrawalStatus Status { get; private set; }
    public string? ApprovedBy { get; private set; }

    /// <summary>The merchant's own approver who signed off a portal-initiated payout (their username), or who
    /// rejected it. Null for an HMAC-API payout, which never passes through merchant approval.</summary>
    public string? MerchantApprovedBy { get; private set; }

    public DateTimeOffset? MerchantApprovedAt { get; private set; }
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

    /// <summary>
    /// The real energy this transaction actually consumed on-chain (TRON's <c>energy_usage_total</c>),
    /// recorded once at <see cref="Confirm"/>. A resource-usage count, NOT a currency amount and NOT related
    /// to <see cref="Fee"/> — that stays exactly what it always meant, the amount charged to the customer.
    /// Null until confirmed; zero for a native transfer (spends no energy).
    /// </summary>
    public BigInteger? EnergyUsed { get; private set; }

    /// <summary>Who audited this merchant settlement, and when (also stamped on an audit rejection). Null for
    /// user payouts, which never enter the audit queue.</summary>
    public string? AuditedBy { get; private set; }

    public DateTimeOffset? AuditedAt { get; private set; }

    /// <summary>Who recorded the external payment, and when. Null until the settlement is completed.</summary>
    public string? CompletedBy { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// The company wallet the admin paid the merchant from — OUTSIDE platform custody, so it is recorded for
    /// audit but never constrained: the admin may pay from whichever wallet suits them. The transaction hash
    /// is what is actually verified on-chain.
    /// </summary>
    public string? SettlementSourceAddress { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a withdrawal in <see cref="WithdrawalStatus.Reserving"/>. The caller then reserves the
    /// funds in the ledger and calls <see cref="ConfirmReserved"/> (success) or
    /// <see cref="MarkReserveFailed"/> (insufficient). Creating first, reserving second — deduped by the
    /// merchant transaction id — means funds are never double-debited nor left reserved without a record.
    /// <paramref name="fee"/> is the on-top platform fee.
    /// </summary>
    public static Result<Withdrawal> Request(
        Guid merchantId,
        Guid assetId,
        Chain chain,
        string destinationAddress,
        BigInteger amount,
        BigInteger fee,
        string merchantTransactionId,
        string? callbackUrl,
        DateTimeOffset now,
        WithdrawalKind kind = WithdrawalKind.User,
        int feeBps = 0,
        BigInteger feeFixed = default,
        BigInteger feeMinimum = default,
        bool minimumFeeApplied = false)
    {
        if (merchantId == Guid.Empty || assetId == Guid.Empty)
            return Result.Failure<Withdrawal>(WithdrawalErrors.OwnerRequired);

        if (string.IsNullOrWhiteSpace(destinationAddress))
            return Result.Failure<Withdrawal>(WithdrawalErrors.DestinationRequired);

        if (amount <= BigInteger.Zero || fee < BigInteger.Zero)
            return Result.Failure<Withdrawal>(WithdrawalErrors.AmountNotPositive);

        if (string.IsNullOrWhiteSpace(merchantTransactionId))
            return Result.Failure<Withdrawal>(WithdrawalErrors.MerchantTransactionIdRequired);

        return Result.Success(new Withdrawal(
            Guid.CreateVersion7(), merchantId, assetId, chain, kind, destinationAddress.Trim(), amount, fee,
            merchantTransactionId.Trim(), string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl.Trim(),
            WithdrawalStatus.Reserving, now, feeBps, feeFixed, feeMinimum, minimumFeeApplied));
    }

    /// <summary>Funds are locked. Moves to PendingApproval above the threshold, otherwise Approved.</summary>
    /// <summary>
    /// The ledger reserve succeeded — decide where the payout waits. <paramref name="requiresMerchantApproval"/>
    /// is set only for portal-initiated payouts (a human submitted it in the merchant's own back office), which
    /// must first clear the MERCHANT's approval before the platform ever evaluates them. An HMAC-API payout
    /// passes false and behaves exactly as before: the merchant's server already authorised it by signing the
    /// request, so it goes straight to the platform threshold decision.
    /// </summary>
    public Result ConfirmReserved(bool requiresApproval, DateTimeOffset now, bool requiresMerchantApproval = false)
    {
        if (Status != WithdrawalStatus.Reserving)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = requiresMerchantApproval
            ? WithdrawalStatus.PendingMerchantApproval
            // A MERCHANT settlement is never paid by this system — an admin pays it from a company wallet
            // outside platform custody and records the result — so it diverts here to the audit queue instead
            // of the automated pipeline. It therefore cannot be blocked by a low hot wallet, and it never
            // consumes a hot-pool wallet. The platform approval threshold still applies first.
            : Kind == WithdrawalKind.Merchant && !requiresApproval
                ? WithdrawalStatus.PendingAdminAudit
                : requiresApproval ? WithdrawalStatus.PendingApproval : WithdrawalStatus.Approved;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// The merchant's own approver signs off a portal-initiated payout. Where it goes next is the platform's
    /// call, not theirs: at or below the approval threshold it is cleared to send automatically; above it, it
    /// still needs platform staff (§10). A merchant can never approve its way past the platform gate.
    /// </summary>
    public Result MerchantApprove(string approvedBy, bool requiresPlatformApproval, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingMerchantApproval)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(approvedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        MerchantApprovedBy = approvedBy.Trim();
        MerchantApprovedAt = now;
        Status = requiresPlatformApproval ? WithdrawalStatus.PendingApproval : WithdrawalStatus.Approved;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>The merchant declines its own payout before the platform sees it — releases the reserve, exactly
    /// as a platform rejection does. Terminal.</summary>
    public Result MerchantReject(string rejectedBy, string reason, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingMerchantApproval)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(rejectedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        MerchantApprovedBy = rejectedBy.Trim();
        FailureReason = reason;
        Status = WithdrawalStatus.Rejected;
        UpdatedAt = now;
        RaiseReleased(reason, now); // return the reserved funds
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

    /// <summary>
    /// Platform staff clear an above-threshold payout to send. This also stamps <see cref="ReleasedAt"/>: an
    /// explicit human approval IS the release, so the processing pass does not park the same payout again on
    /// the identical threshold and demand a second staff action on another screen. The release path stays for
    /// what it is actually for — resuming a payout parked for insufficient hot-wallet float — and the
    /// <c>ReleasedAt is null</c> check still backstops anything that reaches Approved without human review
    /// (e.g. a threshold lowered mid-flight).
    /// </summary>
    public Result Approve(string approvedBy, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingApproval)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        ApprovedBy = approvedBy;
        ReleasedBy = approvedBy;
        ReleasedAt = now;
        // A merchant settlement is paid off-system, so platform approval hands it to the audit queue rather
        // than to the signer. Only a user payout continues into the automated build/sign/broadcast pipeline.
        Status = Kind == WithdrawalKind.Merchant ? WithdrawalStatus.PendingAdminAudit : WithdrawalStatus.Approved;
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
    /// <paramref name="energyUsed"/> is a separate, purely observational figure — how much energy the
    /// transaction actually consumed — recorded on the row but never fed into ledger/fee logic.
    /// </summary>
    public Result Confirm(DateTimeOffset now, BigInteger gasFeeSun = default, Guid? gasAssetId = null, BigInteger energyUsed = default)
    {
        if (Status != WithdrawalStatus.Broadcast)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        Status = WithdrawalStatus.Confirmed;
        EnergyUsed = energyUsed < BigInteger.Zero ? BigInteger.Zero : energyUsed;
        UpdatedAt = now;
        Raise(new WithdrawalConfirmed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), TransactionHash!, now,
            MerchantTransactionId, DestinationAddress, CallbackUrl,
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

    // ── Merchant settlement: audit → external payment → recorded completion ──────────────────────────────
    // This system does not pay a merchant settlement. An operations admin pays it from a company wallet
    // OUTSIDE platform custody and records the verified transaction here. The reserve is held the whole way,
    // so the merchant's money is never at risk of being spent twice or stranded.

    /// <summary>
    /// A platform admin audited the settlement request and cleared it for payment. The finance admin then pays
    /// the merchant externally and records the result via <see cref="RecordFinanceSettlement"/>. Separate from
    /// payment on purpose: reviewing and paying are usually different people, and the ops queue must not
    /// confuse "checked" with "paid".
    /// </summary>
    public Result AdminAuditApprove(string auditedBy, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingAdminAudit)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(auditedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        AuditedBy = auditedBy.Trim();
        AuditedAt = now;
        Status = WithdrawalStatus.PendingFinanceTransfer;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Audit rejected — releases the reserve so the merchant's balance returns to available. Terminal.</summary>
    public Result AdminAuditReject(string auditedBy, string reason, DateTimeOffset now)
    {
        if (Status is not (WithdrawalStatus.PendingAdminAudit or WithdrawalStatus.PendingFinanceTransfer))
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(auditedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        AuditedBy = auditedBy.Trim();
        AuditedAt = now;
        FailureReason = reason;
        Status = WithdrawalStatus.Rejected;
        UpdatedAt = now;
        RaiseReleased(reason, now); // the merchant gets their balance back — a decline never strands funds
        return Result.Success();
    }

    /// <summary>
    /// The finance admin paid the merchant from an external company wallet and recorded the verified on-chain
    /// transaction. Raises <see cref="WithdrawalConfirmed"/> with <c>ExternallySettled: true</c>, so the Ledger
    /// discharges the reserve against <c>ExternalSettlement</c> and leaves <c>TreasuryAsset</c> untouched —
    /// custody genuinely did not move, because the funds never left an address this system watches.
    ///
    /// <para>The caller MUST have verified the hash on-chain first (exists, confirmed, correct destination,
    /// asset and amount). This method records a settled fact; it cannot check the chain itself.</para>
    /// </summary>
    public Result RecordFinanceSettlement(
        string completedBy, string transactionHash, string sourceAddress, DateTimeOffset now)
    {
        if (Status != WithdrawalStatus.PendingFinanceTransfer)
            return Result.Failure(WithdrawalErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(completedBy))
            return Result.Failure(WithdrawalErrors.OwnerRequired);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure(WithdrawalErrors.TransactionHashRequired);

        CompletedBy = completedBy.Trim();
        CompletedAt = now;
        TransactionHash = transactionHash.Trim();
        SettlementSourceAddress = string.IsNullOrWhiteSpace(sourceAddress) ? null : sourceAddress.Trim();
        Status = WithdrawalStatus.FinanceSettled;
        UpdatedAt = now;

        Raise(new WithdrawalConfirmed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee),
            TransactionHash, now, MerchantTransactionId, DestinationAddress, CallbackUrl,
            GasAssetId: null, GasFeeBaseUnits: "0", ExternallySettled: true));
        return Result.Success();
    }

    private void RaiseReleased(string reason, DateTimeOffset now) =>
        Raise(new WithdrawalFailed(
            Guid.CreateVersion7(), now, Id, MerchantId, AssetId, ToBaseUnits(Amount), ToBaseUnits(Fee), reason, now,
            MerchantTransactionId, CallbackUrl));

    private static string ToBaseUnits(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
