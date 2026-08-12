using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

/// <summary>
/// A movement of an asset from one merchant deposit address into the platform hot wallet (concentration).
/// It owns the lifecycle state machine and never touches keys — signing lives behind a port (§10) — and
/// posts <b>nothing</b> to the ledger: a sweep relocates funds between addresses the platform already
/// controls, so custody is unchanged (§15.4, and why Reconciliation sums deposit + hot addresses together).
/// Its only real cost is gas, a platform expense on the deferred Energy/Accounting path.
///
/// Money-safety rules in the transitions mirror Withdrawal's: the signed blob is persisted <em>before</em>
/// broadcast so a crash-and-retry re-broadcasts the same transaction (the chain dedups on tx id) rather than
/// building a fresh one; a failure is only reachable pre-broadcast (nothing left the chain); every transition
/// is guarded, so an out-of-order worker call is refused, not silently applied.
/// </summary>
public sealed class Sweep : Entity<Guid>
{
    private Sweep(
        Guid id, Guid walletId, Chain chain, Guid assetId, string fromAddress, string toAddress,
        BigInteger amount, SweepStatus status, DateTimeOffset now) : base(id)
    {
        WalletId = walletId;
        Chain = chain;
        AssetId = assetId;
        FromAddress = fromAddress;
        ToAddress = toAddress;
        Amount = amount;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Sweep() : base(Guid.Empty)
    {
    }

    /// <summary>The source deposit wallet (Wallet module id). Ties an in-flight sweep to its address.</summary>
    public Guid WalletId { get; private set; }
    public Chain Chain { get; private set; }
    public Guid AssetId { get; private set; }
    public string FromAddress { get; private set; } = null!;
    public string ToAddress { get; private set; } = null!;
    public BigInteger Amount { get; private set; }
    public SweepStatus Status { get; private set; }
    public Guid? SigningRequestId { get; private set; }

    /// <summary>The signed, broadcast-ready transaction blob (public, never key material). Persisted the
    /// moment it is signed so a re-processing pass re-broadcasts the same transaction.</summary>
    public byte[]? SignedTransaction { get; private set; }

    public bool HasSignedTransaction => SignedTransaction is { Length: > 0 };

    public string? TransactionHash { get; private set; }
    public string? FailureReason { get; private set; }
    public int? Confirmations { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a sweep in <see cref="SweepStatus.Pending"/> for the full balance found at a deposit
    /// address. No ledger reserve — the funds are already ours; this only relocates them.</summary>
    public static Result<Sweep> Create(
        Guid walletId, Chain chain, Guid assetId, string fromAddress, string toAddress, BigInteger amount, DateTimeOffset now)
    {
        if (walletId == Guid.Empty)
            return Result.Failure<Sweep>(SweepErrors.WalletRequired);

        if (string.IsNullOrWhiteSpace(fromAddress) || string.IsNullOrWhiteSpace(toAddress))
            return Result.Failure<Sweep>(SweepErrors.AddressRequired);

        if (amount <= BigInteger.Zero)
            return Result.Failure<Sweep>(SweepErrors.AmountNotPositive);

        return Result.Success(new Sweep(
            Guid.CreateVersion7(), walletId, chain, assetId, fromAddress.Trim(), toAddress.Trim(),
            amount, SweepStatus.Pending, now));
    }

    /// <summary>
    /// Records the signed transaction blob and moves Pending → Signing in one step. Persisting the signed
    /// blob before broadcast is the safety guarantee: a transaction's on-chain identity is fixed once signed,
    /// so a crash-and-retry re-broadcasts this exact blob (identical tx id, which the chain dedups) rather than
    /// building a fresh transaction the chain would treat as a distinct send.
    /// </summary>
    public Result RecordSigned(Guid signingRequestId, byte[] signedTransaction, DateTimeOffset now)
    {
        if (Status != SweepStatus.Pending)
            return Result.Failure(SweepErrors.InvalidStateTransition);

        if (signedTransaction is not { Length: > 0 })
            return Result.Failure(SweepErrors.InvalidStateTransition);

        SigningRequestId = signingRequestId;
        SignedTransaction = signedTransaction;
        Status = SweepStatus.Signing;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result MarkBroadcast(string transactionHash, DateTimeOffset now)
    {
        if (Status != SweepStatus.Signing)
            return Result.Failure(SweepErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure(SweepErrors.InvalidStateTransition);

        TransactionHash = transactionHash.Trim();
        Status = SweepStatus.Broadcast;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result RecordConfirmations(int confirmations, DateTimeOffset now)
    {
        if (Status != SweepStatus.Broadcast)
            return Result.Failure(SweepErrors.InvalidStateTransition);

        Confirmations = confirmations < 0 ? 0 : confirmations;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Confirm(DateTimeOffset now)
    {
        if (Status != SweepStatus.Broadcast)
            return Result.Failure(SweepErrors.InvalidStateTransition);

        Status = SweepStatus.Confirmed;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Fails a sweep that has not yet been broadcast (Pending/Signing) — safe, because nothing left the chain
    /// and the deposit address is untouched. Refused once Broadcast: funds may be on-chain, so that is an ops
    /// incident, not an automatic transition.
    /// </summary>
    public Result Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (SweepStatus.Pending or SweepStatus.Signing))
            return Result.Failure(SweepErrors.InvalidStateTransition);

        FailureReason = reason;
        Status = SweepStatus.Failed;
        UpdatedAt = now;
        return Result.Success();
    }
}
