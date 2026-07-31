using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;

/// <summary>Which business transaction a callback delivery record tracks. Notification owns this concept —
/// Deposit/Withdrawal never store delivery outcome themselves (§4.5).</summary>
public enum CallbackReferenceType
{
    Deposit = 1,
    Withdrawal = 2,
}

/// <summary>
/// The delivery lifecycle. <see cref="Pending"/> retries on a fixed backoff schedule; <see cref="Abandoned"/>
/// is reached only after the schedule is exhausted and stops automatic retries, but is not final — an
/// operator can still trigger <see cref="CallbackDelivery.RecordManualResendOutcome"/> against it. This is
/// deliberately a separate concept from the underlying transaction's own status (Deposit/Withdrawal):
/// a transaction can be fully Confirmed while its callback is still Pending, or even permanently Abandoned.
/// </summary>
public enum CallbackDeliveryStatus
{
    Pending = 1,
    Notified = 2,
    Abandoned = 3,
}

/// <summary>
/// Tracks and drives delivery of one merchant callback — one row per <c>(ReferenceType, ReferenceId)</c>,
/// created the moment the underlying event (e.g. <c>PaymentIntentMatched</c>) is first seen. Persists the
/// exact signed request (<see cref="CallbackUrl"/>/<see cref="Body"/>/<see cref="CallbackType"/>/
/// <see cref="Timestamp"/>/<see cref="SignatureHex"/>) so every retry — automatic or manual — resends
/// byte-for-byte the same signed payload, never re-signs (the same idempotency guarantee
/// <c>Withdrawal.SignedTransaction</c> gives the withdrawal broadcast path).
///
/// Retries are bounded: <see cref="CallbackDeliveryProcessingBackoff.Schedule"/> (30s, 1m, 2m, 4m, 10m — 5
/// retries after the immediate first attempt, 6 attempts total) governs <see cref="NextAttemptAt"/>. Once
/// exhausted, the row moves to <see cref="CallbackDeliveryStatus.Abandoned"/> — automatic retries stop, but
/// this is a reporting/ops state, not a dead end: <see cref="RecordManualResendOutcome"/> lets a human
/// trigger one more attempt at any time.
/// </summary>
public sealed class CallbackDelivery : Entity<Guid>
{
    private CallbackDelivery(
        Guid id,
        CallbackReferenceType referenceType,
        Guid referenceId,
        string callbackUrl,
        string body,
        string callbackType,
        string timestamp,
        string signatureHex,
        DateTimeOffset now) : base(id)
    {
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        CallbackUrl = callbackUrl;
        Body = body;
        CallbackType = callbackType;
        Timestamp = timestamp;
        SignatureHex = signatureHex;
        Status = CallbackDeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now; // fire on the worker's very next pass — no artificial delay on the happy path
        CreatedAt = now;
        UpdatedAt = now;
    }

    private CallbackDelivery() : base(Guid.Empty)
    {
    }

    public CallbackReferenceType ReferenceType { get; private set; }
    public Guid ReferenceId { get; private set; }

    /// <summary>The exact signed request, persisted once so every attempt — automatic or manual — resends
    /// identically. Never rebuilt/re-signed after scheduling.</summary>
    public string CallbackUrl { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public string CallbackType { get; private set; } = null!;
    public string Timestamp { get; private set; } = null!;
    public string SignatureHex { get; private set; } = null!;

    public CallbackDeliveryStatus Status { get; private set; }

    /// <summary>Automatic attempts made so far (the immediate first one counts as 1). Manual resends
    /// (<see cref="RecordManualResendOutcome"/>) do not increment this — they are outside the backoff schedule.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the worker should try next. Null once terminal (<see cref="CallbackDeliveryStatus.Notified"/>
    /// or <see cref="CallbackDeliveryStatus.Abandoned"/>) — a manual resend does not requeue automatic retries.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? FirstAttemptedAt { get; private set; }
    public DateTimeOffset? LastAttemptedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates the row the moment a callback becomes due — the first attempt fires immediately
    /// (<see cref="NextAttemptAt"/> = now), never delayed by the retry schedule.</summary>
    public static CallbackDelivery Schedule(
        CallbackReferenceType referenceType,
        Guid referenceId,
        string callbackUrl,
        string body,
        string callbackType,
        string timestamp,
        string signatureHex,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7(), referenceType, referenceId, callbackUrl, body, callbackType, timestamp, signatureHex, now);

    /// <summary>Called by the worker only on rows it selected as due — always a valid transition.</summary>
    public void RecordSuccess(DateTimeOffset now)
    {
        Status = CallbackDeliveryStatus.Notified;
        FirstAttemptedAt ??= now;
        LastAttemptedAt = now;
        DeliveredAt = now;
        NextAttemptAt = null;
        LastError = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Called by the worker only on rows it selected as due. Schedules the next attempt per
    /// <paramref name="backoffSchedule"/>, or abandons once every scheduled retry is exhausted.
    /// </summary>
    public void RecordFailure(string error, DateTimeOffset now, IReadOnlyList<TimeSpan> backoffSchedule)
    {
        AttemptCount++;
        FirstAttemptedAt ??= now;
        LastAttemptedAt = now;
        LastError = error;
        UpdatedAt = now;

        if (AttemptCount > backoffSchedule.Count)
        {
            Status = CallbackDeliveryStatus.Abandoned;
            NextAttemptAt = null;
            return;
        }

        NextAttemptAt = now + backoffSchedule[AttemptCount - 1];
    }

    /// <summary>
    /// An operator manually retriggers a delivery that automatic retries gave up on. Only valid from
    /// <see cref="CallbackDeliveryStatus.Abandoned"/> — success moves it to <see cref="CallbackDeliveryStatus.Notified"/>;
    /// failure leaves it Abandoned (available to retry manually again) rather than re-entering the automatic
    /// backoff schedule.
    /// </summary>
    public Result RecordManualResendOutcome(bool succeeded, string? error, DateTimeOffset now)
    {
        if (Status != CallbackDeliveryStatus.Abandoned)
            return Result.Failure(CallbackDeliveryErrors.NotAbandoned);

        LastAttemptedAt = now;
        UpdatedAt = now;

        if (succeeded)
        {
            Status = CallbackDeliveryStatus.Notified;
            DeliveredAt = now;
            LastError = null;
        }
        else
        {
            LastError = error;
        }

        return Result.Success();
    }
}

/// <summary>The fixed backoff schedule for automatic retries: 30s, 1m, 2m, 4m, 10m after the immediate
/// first attempt — 5 retries, 6 attempts total, then <see cref="CallbackDeliveryStatus.Abandoned"/>.</summary>
public static class CallbackDeliveryProcessingBackoff
{
    public static readonly IReadOnlyList<TimeSpan> Schedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(10),
    ];
}
