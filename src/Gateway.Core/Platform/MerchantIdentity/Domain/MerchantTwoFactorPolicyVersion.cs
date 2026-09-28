using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

/// <summary>
/// One version of a merchant-portal 2FA action list — which portal actions demand a fresh authenticator code.
///
/// <para><b>Two layers in one table, told apart by <see cref="MerchantId"/>:</b></para>
/// <list type="bullet">
/// <item><c>MerchantId = null</c> — the <b>platform minimum</b>, set by platform staff in the admin back office.
/// Every merchant is bound by it and no merchant can switch any of it off.</item>
/// <item><c>MerchantId = X</c> — merchant X's <b>own additions</b>, set by X's admin in the merchant portal.
/// They can only ADD actions on top of the platform minimum, never remove one.</item>
/// </list>
/// The effective list for a merchant is the union of the two (plus the always-on self-protecting action) — see
/// <c>MerchantTwoFactorPolicyProvider</c>. A merchant can therefore never be less protected than the platform
/// requires, while still being free to be stricter.
///
/// <para><b>Append-only</b>, like the staff policy (<c>identity.TwoFactorPolicyVersion</c>): a change inserts a
/// row and nothing is updated in place, so an action taken last month stays explainable against the list in
/// force then.</para>
///
/// <para>The action codes are opaque strings from <c>MerchantGuardedActions</c>. No ledger impact and no keys (§10).</para>
/// </summary>
public sealed class MerchantTwoFactorPolicyVersion : Entity<Guid>
{
    private const char Separator = '|';

    private MerchantTwoFactorPolicyVersion(
        Guid id, Guid? merchantId, string guardedActionsCsv, string? note, string updatedBy, DateTimeOffset updatedAt)
        : base(id)
    {
        MerchantId = merchantId;
        GuardedActionsCsv = guardedActionsCsv;
        Note = note;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    private MerchantTwoFactorPolicyVersion() : base(Guid.Empty)
    {
    }

    /// <summary>Null for the platform minimum; the tenant for a merchant's own additions.</summary>
    public Guid? MerchantId { get; private set; }

    /// <summary>Action codes joined with '|'. For a merchant row these are ONLY the additions — never the
    /// platform-required ones, so a later relaxation of the platform minimum is not silently undone by an old
    /// merchant row that merely echoed it.</summary>
    public string GuardedActionsCsv { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    /// <summary>Who saved it — a staff username for the platform minimum, a portal username for a merchant row.
    /// Taken from the validated session, never from the request body.</summary>
    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsPlatformMinimum => MerchantId is null;

    public IReadOnlyList<string> GuardedActions() =>
        string.IsNullOrEmpty(GuardedActionsCsv)
            ? []
            : GuardedActionsCsv.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Result<MerchantTwoFactorPolicyVersion> Create(
        Guid? merchantId, IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        DateTimeOffset updatedAt)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<MerchantTwoFactorPolicyVersion>(MerchantTwoFactorPolicyErrors.MerchantRequired);

        if (string.IsNullOrWhiteSpace(updatedBy))
            return Result.Failure<MerchantTwoFactorPolicyVersion>(MerchantTwoFactorPolicyErrors.UpdatedByRequired);

        if (guardedActions.Any(a => a is null || a.Contains(Separator)))
            return Result.Failure<MerchantTwoFactorPolicyVersion>(MerchantTwoFactorPolicyErrors.InvalidActionCode);

        var normalized = guardedActions
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return Result.Success(new MerchantTwoFactorPolicyVersion(
            Guid.CreateVersion7(),
            merchantId,
            string.Join(Separator, normalized),
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            updatedBy.Trim(),
            updatedAt));
    }
}

public static class MerchantTwoFactorPolicyErrors
{
    public static readonly Error MerchantRequired =
        Error.Validation("merchant_two_factor_policy.merchant_required", "A merchant is required.");

    public static readonly Error UpdatedByRequired =
        Error.Validation("merchant_two_factor_policy.updated_by_required", "The saving user is required.");

    public static readonly Error InvalidActionCode =
        Error.Validation("merchant_two_factor_policy.invalid_action_code", "An action code is malformed.");

    /// <summary>A code the portal does not enforce. Refused rather than stored: a checkbox that guards nothing
    /// reads as protection that is not there.</summary>
    public static readonly Error UnknownAction =
        Error.Validation("merchant_two_factor_policy.unknown_action", "One or more action codes are not recognised.");
}
