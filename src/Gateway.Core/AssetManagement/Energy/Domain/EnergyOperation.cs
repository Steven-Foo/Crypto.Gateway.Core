using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;

/// <summary>
/// One on-chain energy operation — a stake (freeze TRX for energy) or a delegate (lend staked energy to a
/// target address). It owns the lifecycle state machine and never touches keys (signing lives behind a port,
/// §10), and posts NOTHING to the ledger: staking/delegation lock recoverable TRX, they don't spend value
/// (§15.4, same principle as Sweep). The signed blob is persisted before broadcast so a crash-and-retry
/// re-broadcasts the same transaction (the chain dedups on tx id); a failure is only reachable pre-broadcast.
/// </summary>
public sealed class EnergyOperation : Entity<Guid>
{
    private EnergyOperation(
        Guid id, EnergyOperationKind kind, Chain chain, Guid stakingWalletId, string ownerAddress,
        string? targetAddress, BigInteger amountSun, EnergyOperationStatus status, DateTimeOffset now) : base(id)
    {
        Kind = kind;
        Chain = chain;
        StakingWalletId = stakingWalletId;
        OwnerAddress = ownerAddress;
        TargetAddress = targetAddress;
        AmountSun = amountSun;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private EnergyOperation() : base(Guid.Empty)
    {
    }

    public EnergyOperationKind Kind { get; private set; }
    public Chain Chain { get; private set; }

    /// <summary>The platform staking wallet (Wallet module id) whose key signs this operation.</summary>
    public Guid StakingWalletId { get; private set; }

    /// <summary>The staking wallet's address (owner of the freeze/delegate).</summary>
    public string OwnerAddress { get; private set; } = null!;

    /// <summary>The delegation receiver (e.g. a deposit address). Null for a stake.</summary>
    public string? TargetAddress { get; private set; }

    /// <summary>TRX amount in sun: frozen (Stake) or delegated (Delegate). Base units (§14).</summary>
    public BigInteger AmountSun { get; private set; }

    public EnergyOperationStatus Status { get; private set; }
    public Guid? SigningRequestId { get; private set; }

    /// <summary>The signed, broadcast-ready blob (public, never key material). Persisted the moment it is
    /// signed so a re-processing pass re-broadcasts the same transaction.</summary>
    public byte[]? SignedTransaction { get; private set; }

    public bool HasSignedTransaction => SignedTransaction is { Length: > 0 };

    public string? TransactionHash { get; private set; }
    public string? FailureReason { get; private set; }
    public int? Confirmations { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<EnergyOperation> CreateStake(
        Guid stakingWalletId, Chain chain, string ownerAddress, BigInteger trxAmountSun, DateTimeOffset now)
    {
        if (stakingWalletId == Guid.Empty || string.IsNullOrWhiteSpace(ownerAddress))
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationOwnerRequired);

        if (trxAmountSun <= BigInteger.Zero)
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationAmountNotPositive);

        return Result.Success(new EnergyOperation(
            Guid.CreateVersion7(), EnergyOperationKind.Stake, chain, stakingWalletId, ownerAddress.Trim(),
            targetAddress: null, trxAmountSun, EnergyOperationStatus.Pending, now));
    }

    public static Result<EnergyOperation> CreateDelegate(
        Guid stakingWalletId, Chain chain, string ownerAddress, string receiverAddress, BigInteger trxAmountSun, DateTimeOffset now)
    {
        if (stakingWalletId == Guid.Empty || string.IsNullOrWhiteSpace(ownerAddress))
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationOwnerRequired);

        if (string.IsNullOrWhiteSpace(receiverAddress))
            return Result.Failure<EnergyOperation>(EnergyErrors.DelegateReceiverRequired);

        if (trxAmountSun <= BigInteger.Zero)
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationAmountNotPositive);

        return Result.Success(new EnergyOperation(
            Guid.CreateVersion7(), EnergyOperationKind.Delegate, chain, stakingWalletId, ownerAddress.Trim(),
            receiverAddress.Trim(), trxAmountSun, EnergyOperationStatus.Pending, now));
    }

    /// <summary>
    /// A native-TRX top-up from the staking (gas hub) wallet to <paramref name="receiverAddress"/>, so the
    /// receiver can pay bandwidth. A real transfer, but between platform-controlled addresses (custody-internal
    /// ⇒ no ledger entry, §15.4). Shares the sign→broadcast→confirm lifecycle with stake/delegate.
    /// </summary>
    public static Result<EnergyOperation> CreateTopUp(
        Guid stakingWalletId, Chain chain, string ownerAddress, string receiverAddress, BigInteger trxAmountSun, DateTimeOffset now)
    {
        if (stakingWalletId == Guid.Empty || string.IsNullOrWhiteSpace(ownerAddress))
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationOwnerRequired);

        if (string.IsNullOrWhiteSpace(receiverAddress))
            return Result.Failure<EnergyOperation>(EnergyErrors.DelegateReceiverRequired);

        if (trxAmountSun <= BigInteger.Zero)
            return Result.Failure<EnergyOperation>(EnergyErrors.OperationAmountNotPositive);

        return Result.Success(new EnergyOperation(
            Guid.CreateVersion7(), EnergyOperationKind.TopUp, chain, stakingWalletId, ownerAddress.Trim(),
            receiverAddress.Trim(), trxAmountSun, EnergyOperationStatus.Pending, now));
    }

    /// <summary>Records the signed blob and moves Pending → Signing (persist-before-broadcast safety).</summary>
    public Result RecordSigned(Guid signingRequestId, byte[] signedTransaction, DateTimeOffset now)
    {
        if (Status != EnergyOperationStatus.Pending)
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        if (signedTransaction is not { Length: > 0 })
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        SigningRequestId = signingRequestId;
        SignedTransaction = signedTransaction;
        Status = EnergyOperationStatus.Signing;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result MarkBroadcast(string transactionHash, DateTimeOffset now)
    {
        if (Status != EnergyOperationStatus.Signing)
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        TransactionHash = transactionHash.Trim();
        Status = EnergyOperationStatus.Broadcast;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result RecordConfirmations(int confirmations, DateTimeOffset now)
    {
        if (Status != EnergyOperationStatus.Broadcast)
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        Confirmations = confirmations < 0 ? 0 : confirmations;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Confirm(DateTimeOffset now)
    {
        if (Status != EnergyOperationStatus.Broadcast)
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        Status = EnergyOperationStatus.Confirmed;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Fails a pre-broadcast operation (Pending/Signing) — safe, nothing left the chain. Refused once
    /// broadcast (the freeze/delegate may have taken effect), which is then an ops concern.</summary>
    public Result Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (EnergyOperationStatus.Pending or EnergyOperationStatus.Signing))
            return Result.Failure(EnergyErrors.OperationInvalidStateTransition);

        FailureReason = reason;
        Status = EnergyOperationStatus.Failed;
        UpdatedAt = now;
        return Result.Success();
    }
}
