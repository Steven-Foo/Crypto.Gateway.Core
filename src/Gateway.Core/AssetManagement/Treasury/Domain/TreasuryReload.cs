using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

/// <summary>
/// A treasury→hot reload: moves funds from the cold treasury (source) into one hot-pool wallet (target). The
/// cold key is not in the system, so the aggregate carries the <em>unsigned</em> transaction the operator signs
/// client-side, then the <em>signed</em> blob the backend broadcasts — never a key (§10). It moves funds between
/// two platform-controlled addresses, so it raises no events and touches no ledger (§14): custody total is
/// unchanged, only its location.
///
/// State machine (mirrors Withdrawal/Sweep, minus the reserve): the signed blob is persisted before broadcast,
/// so a crash-and-retry re-broadcasts the same transaction (the chain dedups on its id) rather than building a
/// new one. Fail is reachable only before broadcast.
/// </summary>
public sealed class TreasuryReload : Entity<Guid>
{
    private TreasuryReload(
        Guid id, Chain chain, Guid assetId, string sourceAddress, Guid targetWalletId, string targetAddress,
        BigInteger amount, byte[] unsignedPayload, DateTimeOffset now) : base(id)
    {
        Chain = chain;
        AssetId = assetId;
        SourceAddress = sourceAddress;
        TargetWalletId = targetWalletId;
        TargetAddress = targetAddress;
        Amount = amount;
        UnsignedPayload = unsignedPayload;
        Status = TreasuryReloadStatus.AwaitingSignature;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private TreasuryReload() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public Guid AssetId { get; private set; }

    /// <summary>The cold treasury address the funds move FROM.</summary>
    public string SourceAddress { get; private set; } = null!;

    /// <summary>The hot-pool wallet (and its address) the funds move TO — chosen by the operator.</summary>
    public Guid TargetWalletId { get; private set; }
    public string TargetAddress { get; private set; } = null!;

    public BigInteger Amount { get; private set; }
    public TreasuryReloadStatus Status { get; private set; }

    /// <summary>The unsigned transaction to be signed client-side. Public, non-key material.</summary>
    public byte[] UnsignedPayload { get; private set; } = null!;

    /// <summary>The operator-signed, broadcast-ready blob — persisted before broadcast so a retry re-broadcasts
    /// the same transaction. Opaque bytes, never key material (§10).</summary>
    public byte[]? SignedTransaction { get; private set; }

    public bool HasSignedTransaction => SignedTransaction is { Length: > 0 };

    public string? TransactionHash { get; private set; }
    public int? Confirmations { get; private set; }
    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TreasuryReload> Initiate(
        Chain chain, Guid assetId, string sourceAddress, Guid targetWalletId, string targetAddress,
        BigInteger amount, byte[] unsignedPayload, DateTimeOffset now)
    {
        if (amount <= BigInteger.Zero)
            return Result.Failure<TreasuryReload>(TreasuryReloadErrors.AmountNotPositive);

        if (string.IsNullOrWhiteSpace(sourceAddress) || string.IsNullOrWhiteSpace(targetAddress))
            return Result.Failure<TreasuryReload>(TreasuryReloadErrors.AddressRequired);

        if (targetWalletId == Guid.Empty)
            return Result.Failure<TreasuryReload>(TreasuryReloadErrors.TargetWalletRequired);

        if (unsignedPayload is not { Length: > 0 })
            return Result.Failure<TreasuryReload>(TreasuryReloadErrors.UnsignedPayloadRequired);

        return Result.Success(new TreasuryReload(
            Guid.CreateVersion7(), chain, assetId, sourceAddress.Trim(), targetWalletId, targetAddress.Trim(),
            amount, unsignedPayload, now));
    }

    /// <summary>Records the operator's signed blob (→ <see cref="TreasuryReloadStatus.Signed"/>). The key never
    /// reaches the backend; only this signed, broadcastable result does (§10).</summary>
    public Result SubmitSigned(byte[] signedTransaction, DateTimeOffset now)
    {
        if (Status != TreasuryReloadStatus.AwaitingSignature)
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        if (signedTransaction is not { Length: > 0 })
            return Result.Failure(TreasuryReloadErrors.SignedPayloadRequired);

        SignedTransaction = signedTransaction;
        Status = TreasuryReloadStatus.Signed;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result MarkBroadcast(string transactionHash, DateTimeOffset now)
    {
        if (Status != TreasuryReloadStatus.Signed)
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        TransactionHash = transactionHash.Trim();
        Status = TreasuryReloadStatus.Broadcast;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result RecordConfirmations(int confirmations, DateTimeOffset now)
    {
        if (Status != TreasuryReloadStatus.Broadcast)
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        Confirmations = confirmations < 0 ? 0 : confirmations;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Confirms a broadcast reload — the target hot wallet's float has increased on-chain. No event,
    /// no ledger entry: custody total is unchanged (both addresses are platform-controlled, §14).</summary>
    public Result Confirm(DateTimeOffset now)
    {
        if (Status != TreasuryReloadStatus.Broadcast)
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        Status = TreasuryReloadStatus.Confirmed;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Abandons a reload that has not been broadcast (nothing left the chain). Refused once broadcast.</summary>
    public Result Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (TreasuryReloadStatus.AwaitingSignature or TreasuryReloadStatus.Signed))
            return Result.Failure(TreasuryReloadErrors.InvalidStateTransition);

        StatusReason = reason;
        Status = TreasuryReloadStatus.Failed;
        UpdatedAt = now;
        return Result.Success();
    }
}
