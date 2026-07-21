using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>
/// A deposit invoice: a merchant asks a payer for <see cref="ExpectedAmount"/> to a dedicated address and
/// tracks whether it arrives. It is a merchant-facing overlay on top of on-chain detection, <b>not</b> in the
/// money path — the Ledger credits any confirmed deposit to the address's owning merchant independently, so a
/// missing or mismatched intent never blocks a credit. This aggregate only records expectation and outcome.
///
/// Correctness rules:
/// <list type="bullet">
///   <item>idempotent per merchant reference — <c>(MerchantId, MerchantTransactionId)</c> is UNIQUE (§7.3);</item>
///   <item>at most one <em>live</em> (Waiting, unexpired) intent may hold a given address at a time —
///   a filtered UNIQUE index on the address is the arbiter, so an address is reused only once it is free;</item>
///   <item>matching is idempotent — a redelivered <c>DepositConfirmed</c> cannot double-resolve an intent.</item>
/// </list>
/// </summary>
public sealed class PaymentIntent : Entity<Guid>
{
    private PaymentIntent(
        Guid id,
        Guid publicReference,
        Guid merchantId,
        string merchantTransactionId,
        Chain chain,
        Guid assetId,
        Guid walletId,
        string address,
        BigInteger expectedAmount,
        string? callbackUrl,
        DateTimeOffset expiresAt,
        DateTimeOffset graceExpiresAt,
        DateTimeOffset now) : base(id)
    {
        PublicReference = publicReference;
        MerchantId = merchantId;
        MerchantTransactionId = merchantTransactionId;
        Chain = chain;
        AssetId = assetId;
        WalletId = walletId;
        Address = address;
        ExpectedAmount = expectedAmount;
        CallbackUrl = callbackUrl;
        Status = PaymentIntentStatus.Waiting;
        ExpiresAt = expiresAt;
        GraceExpiresAt = graceExpiresAt;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private PaymentIntent() : base(Guid.Empty)
    {
    }

    /// <summary>The unguessable public id shown to the payer and used in the <c>/pay/{ref}</c> URL (never the PK).</summary>
    public Guid PublicReference { get; private set; }

    public Guid MerchantId { get; private set; }

    /// <summary>The merchant's own transaction reference — the client idempotency key, unique per merchant.</summary>
    public string MerchantTransactionId { get; private set; } = null!;

    public Chain Chain { get; private set; }
    public Guid AssetId { get; private set; }

    /// <summary>The dedicated deposit address assigned to this invoice (opaque wallet id + its public address).</summary>
    public Guid WalletId { get; private set; }
    public string Address { get; private set; } = null!;

    /// <summary>The amount the payer is asked to send, in the asset's base units.</summary>
    public BigInteger ExpectedAmount { get; private set; }

    public string? CallbackUrl { get; private set; }
    public PaymentIntentStatus Status { get; private set; }
    public Guid? MatchedDepositId { get; private set; }

    /// <summary>Set at match: whether the confirmed amount met the expectation exactly (base units). Null until matched.</summary>
    public bool? AmountMatched { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The hard cutoff (<c>ExpiresAt</c> + grace) after which the address actually frees up. Between
    /// <see cref="ExpiresAt"/> and this, the payer is shown "expired" but the wallet stays reserved and a
    /// late-confirming transfer still matches — see <c>PaymentIntentOptions.GraceMinutes</c>.</summary>
    public DateTimeOffset GraceExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsWaiting => Status == PaymentIntentStatus.Waiting;

    public static Result<PaymentIntent> Create(
        Guid merchantId,
        string merchantTransactionId,
        Chain chain,
        Guid assetId,
        Guid walletId,
        string address,
        BigInteger expectedAmount,
        string? callbackUrl,
        DateTimeOffset expiresAt,
        DateTimeOffset graceExpiresAt,
        DateTimeOffset now)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.MerchantRequired);

        if (string.IsNullOrWhiteSpace(merchantTransactionId))
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.ReferenceRequired);

        if (assetId == Guid.Empty)
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.AssetRequired);

        if (walletId == Guid.Empty || string.IsNullOrWhiteSpace(address))
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.WalletRequired);

        if (expectedAmount <= BigInteger.Zero)
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.AmountNotPositive);

        if (expiresAt <= now)
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.ExpiryInPast);

        if (graceExpiresAt < expiresAt)
            return Result.Failure<PaymentIntent>(PaymentIntentErrors.GraceExpiryBeforeExpiry);

        return Result.Success(new PaymentIntent(
            Guid.CreateVersion7(), Guid.NewGuid(), merchantId, merchantTransactionId.Trim(), chain, assetId,
            walletId, address.Trim(), expectedAmount, callbackUrl, expiresAt, graceExpiresAt, now));
    }

    /// <summary>
    /// Matches a confirmed deposit to this waiting invoice and raises <see cref="PaymentIntentMatched"/> for the
    /// callback path. Idempotent: a redelivered event, or a match on an already-resolved intent, is a no-op that
    /// preserves the first outcome and raises nothing. Records whether the paid amount met the expectation exactly
    /// — a mismatch still matches (the merchant decides), mirroring the reference flow.
    /// </summary>
    public Result MatchTo(Guid depositId, string transactionHash, BigInteger actualAmount, DateTimeOffset now)
    {
        if (Status != PaymentIntentStatus.Waiting)
            return Result.Success();

        Status = PaymentIntentStatus.Matched;
        MatchedDepositId = depositId;
        AmountMatched = actualAmount == ExpectedAmount;
        UpdatedAt = now;

        Raise(new PaymentIntentMatched(
            Guid.CreateVersion7(), now, MerchantId, PublicReference, MerchantTransactionId, CallbackUrl,
            Chain, AssetId, Address, ExpectedAmount.ToString(CultureInfo.InvariantCulture),
            actualAmount.ToString(CultureInfo.InvariantCulture), AmountMatched.Value, depositId, transactionHash, now));

        return Result.Success();
    }

    /// <summary>Marks an unpaid invoice expired, freeing its address for reuse. Idempotent; only a waiting intent expires.</summary>
    public Result Expire(DateTimeOffset now)
    {
        if (Status != PaymentIntentStatus.Waiting)
            return Result.Success();

        Status = PaymentIntentStatus.Expired;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Staff-initiated cancellation (e.g. a test invoice). Unlike <see cref="Expire"/>/<see cref="MatchTo"/>
    /// this is an explicit action with a caller expecting feedback, so an invalid transition is a failure,
    /// not a silent no-op — only a still-<see cref="PaymentIntentStatus.Waiting"/> invoice can be failed;
    /// no money has moved for it, so there is nothing to reverse in the Ledger.
    /// </summary>
    public Result Fail(string reason, DateTimeOffset now)
    {
        if (Status != PaymentIntentStatus.Waiting)
            return Result.Failure(PaymentIntentErrors.InvalidStateTransition);

        Status = PaymentIntentStatus.Failed;
        UpdatedAt = now;

        Raise(new PaymentIntentFailed(
            Guid.CreateVersion7(), now, MerchantId, PublicReference, MerchantTransactionId, CallbackUrl, reason, now));

        return Result.Success();
    }
}
