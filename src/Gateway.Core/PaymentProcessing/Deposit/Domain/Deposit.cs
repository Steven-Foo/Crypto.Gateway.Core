using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;

/// <summary>
/// An on-chain deposit to a watched address, tracked from detection through confirmation. It is the
/// Deposit module's aggregate root; it decides <em>when</em> money is safe to credit but never touches
/// the ledger itself — it raises <see cref="DepositConfirmed"/>/<see cref="DepositOrphaned"/>, which the
/// outbox delivers to the Ledger (§4.5, §7.5).
///
/// Correctness rules baked in:
/// <list type="bullet">
///   <item>credit only at the policy threshold, never on first sight — a pre-confirmation reorg costs nothing;</item>
///   <item>a reversal is raised only if the deposit had actually been confirmed (i.e. credited);</item>
///   <item>state transitions are idempotent, so a worker may re-process the same deposit safely.</item>
/// </list>
/// Deduplication is the DB's job: <c>(Chain, TransactionHash, OutputIndex)</c> is UNIQUE (§7.3).
/// </summary>
public sealed class Deposit : Entity<Guid>
{
    private Deposit(
        Guid id,
        Chain chain,
        string address,
        Guid walletId,
        Guid merchantId,
        Guid assetId,
        BigInteger amount,
        string transactionHash,
        int outputIndex,
        long blockNumber,
        string blockHash,
        DepositStatus status,
        DateTimeOffset now) : base(id)
    {
        Chain = chain;
        Address = address;
        WalletId = walletId;
        MerchantId = merchantId;
        AssetId = assetId;
        Amount = amount;
        TransactionHash = transactionHash;
        OutputIndex = outputIndex;
        BlockNumber = blockNumber;
        BlockHash = blockHash;
        Status = status;
        Confirmations = 0;
        DetectedAt = now;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Deposit() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;
    public Guid WalletId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid AssetId { get; private set; }
    public BigInteger Amount { get; private set; }
    public string TransactionHash { get; private set; } = null!;
    public int OutputIndex { get; private set; }
    public long BlockNumber { get; private set; }
    public string BlockHash { get; private set; } = null!;
    public DepositStatus Status { get; private set; }
    public int Confirmations { get; private set; }
    public DateTimeOffset DetectedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>
    /// When the carrying block passed the chain's irreversibility point, after which this deposit is retired
    /// from reorg watching. Null while still watched. This is a <em>tracking</em> marker, not a money state —
    /// <see cref="Status"/> remains the financial lifecycle.
    /// </summary>
    public DateTimeOffset? FinalizedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsConfirmed => Status == DepositStatus.Confirmed;
    public bool IsPending => Status == DepositStatus.Detected;

    /// <summary>Whether the deposit is beyond reorg reach and no longer needs watching.</summary>
    public bool IsFinalized => FinalizedAt is not null;

    /// <summary>
    /// Records a newly-detected transfer. An amount below the policy minimum is recorded as
    /// <see cref="DepositStatus.Ignored"/> (dust) — kept for audit and to avoid re-evaluating it,
    /// but never credited.
    /// </summary>
    public static Result<Deposit> Record(
        Chain chain,
        string address,
        Guid walletId,
        Guid merchantId,
        Guid assetId,
        BigInteger amount,
        string transactionHash,
        int outputIndex,
        long blockNumber,
        string blockHash,
        DepositPolicy policy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<Deposit>(DepositErrors.AddressRequired);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure<Deposit>(DepositErrors.TransactionHashRequired);

        if (amount <= BigInteger.Zero)
            return Result.Failure<Deposit>(DepositErrors.AmountNotPositive);

        if (walletId == Guid.Empty || merchantId == Guid.Empty || assetId == Guid.Empty)
            return Result.Failure<Deposit>(DepositErrors.OwnerRequired);

        var status = policy.MeetsMinimum(amount) ? DepositStatus.Detected : DepositStatus.Ignored;

        return Result.Success(new Deposit(
            Guid.CreateVersion7(), chain, address.Trim(), walletId, merchantId, assetId, amount,
            transactionHash.Trim(), outputIndex, blockNumber, blockHash, status, now));
    }

    /// <summary>
    /// Folds the latest chain progress in. Once the policy threshold is met the deposit becomes
    /// <see cref="DepositStatus.Confirmed"/> and raises <see cref="DepositConfirmed"/> exactly once.
    /// A no-op if the deposit is no longer pending (already confirmed, orphaned, or ignored).
    /// </summary>
    public Result RegisterConfirmations(int confirmations, bool isFinalized, DepositPolicy policy, DateTimeOffset now)
    {
        if (Status != DepositStatus.Detected)
            return Result.Success();

        Confirmations = confirmations < 0 ? 0 : confirmations;
        UpdatedAt = now;

        if (policy.IsCreditable(Confirmations, isFinalized))
        {
            Status = DepositStatus.Confirmed;
            ConfirmedAt = now;
            Raise(new DepositConfirmed(
                Guid.CreateVersion7(), now, Id, WalletId, MerchantId, AssetId, AmountString, Chain, TransactionHash, OutputIndex, now));
        }

        return Result.Success();
    }

    /// <summary>
    /// Retires the deposit from reorg watching once its block is buried beyond the chain's own
    /// irreversibility point (Tron's solidified block, Ethereum's finalized checkpoint). Such a block can
    /// never be reorged, so re-checking it costs one RPC per deposit per pass forever and could never change
    /// the outcome.
    ///
    /// <para>Only a credited deposit settles: one still <see cref="DepositStatus.Detected"/> must keep being
    /// tracked until it reaches the confirmation threshold, even when its block is already final — otherwise
    /// a chain whose finality arrives before the policy depth would strand it uncredited. Idempotent.</para>
    /// </summary>
    public Result MarkFinalized(DateTimeOffset now)
    {
        if (Status != DepositStatus.Confirmed || FinalizedAt is not null)
            return Result.Success();

        FinalizedAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Marks the deposit as orphaned by a reorg. A compensating <see cref="DepositOrphaned"/> is raised
    /// only if the deposit had been confirmed (i.e. credited) — a still-pending deposit orphans silently
    /// because nothing was ever posted to the ledger. Idempotent.
    /// </summary>
    public Result MarkOrphaned(DateTimeOffset now)
    {
        if (Status is DepositStatus.Orphaned or DepositStatus.Ignored)
            return Result.Success();

        var wasCredited = Status == DepositStatus.Confirmed;
        Status = DepositStatus.Orphaned;
        UpdatedAt = now;

        if (wasCredited)
        {
            Raise(new DepositOrphaned(
                Guid.CreateVersion7(), now, Id, MerchantId, AssetId, AmountString, Chain, TransactionHash, OutputIndex, now));
        }

        return Result.Success();
    }

    /// <summary>Exact base-unit magnitude as an invariant integer string — lossless across the event transport (§14).</summary>
    private string AmountString => Amount.ToString(CultureInfo.InvariantCulture);
}
