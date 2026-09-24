using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// One version of the platform-wide list of actions that demand a second factor, as chosen by staff in the
/// back office.
///
/// <para><b>Platform-wide by design.</b> When one admin marks an action as guarded, every admin must produce
/// their own code to perform it. There is no per-account exemption: a control that some operators can skip
/// is a control that does nothing for exactly the accounts an attacker will prefer.</para>
///
/// <para><b>Append-only.</b> Changing the policy inserts a new row; nothing is updated in place. An action
/// taken last month has to stay explainable, and "was a code required when that top-up was recorded" cannot
/// be answered from a mutable settings row. Same discipline as
/// <c>compliance.ScreeningPolicyVersion</c>, for the same reason.</para>
///
/// <para><b>The action codes are opaque strings.</b> They are owned and interpreted by the host that enforces
/// them (<c>GuardedActions</c> in OperationsApi); Identity stores and matches them and knows nothing about
/// Treasury, Withdrawal or Compliance (§4.5). That is also what makes the feature expandable: a new guarded
/// action is a constant plus one line on a route, with no change here and no migration.</para>
///
/// <para>No ledger impact and no keys (§10).</para>
/// </summary>
public sealed class TwoFactorPolicyVersion : Entity<Guid>
{
    /// <summary>
    /// The action that protects the control itself. It is ALWAYS guarded, whatever a saved version says, and
    /// <see cref="Create"/> forces it into every version.
    ///
    /// <para>Without this the control unlocks itself: anyone on a stolen admin session opens the settings
    /// screen, unticks every action, and every code prompt is gone. Hardcoding it means weakening 2FA always
    /// costs a fresh code from a real authenticator. The same reasoning makes the shipped sanctions
    /// designations add-only in the screening policy — the rule that must never come off in a web form is
    /// the one that protects the rest.</para>
    ///
    /// <para>The literal is duplicated in the host's <c>GuardedActions.TwoFactorPolicy</c>; a composition
    /// test asserts the two agree, because Identity may not reference the host to share the constant.</para>
    /// </summary>
    public const string SelfProtectingAction = "ops.security.two-factor-policy";

    private const char Separator = '|';

    private TwoFactorPolicyVersion(
        Guid id, string guardedActionsCsv, string? note, string updatedBy, DateTimeOffset updatedAt) : base(id)
    {
        GuardedActionsCsv = guardedActionsCsv;
        Note = note;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    private TwoFactorPolicyVersion() : base(Guid.Empty)
    {
    }

    /// <summary>Guarded action codes joined with '|'. Pipe rather than comma because a code is an opaque
    /// host-owned string and a comma is likelier to appear inside one.</summary>
    public string GuardedActionsCsv { get; private set; } = string.Empty;

    /// <summary>Why the change was made. Optional, and the difference between a readable history and a list
    /// of checkbox states nobody can account for.</summary>
    public string? Note { get; private set; }

    /// <summary>Who set it, taken from the validated session and never from the request body — an
    /// attribution the caller supplied is not an attribution.</summary>
    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> GuardedActions() =>
        string.IsNullOrEmpty(GuardedActionsCsv)
            ? []
            : GuardedActionsCsv.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Result<TwoFactorPolicyVersion> Create(
        IReadOnlyCollection<string> guardedActions, string? note, string updatedBy, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedBy))
            return Result.Failure<TwoFactorPolicyVersion>(TwoFactorErrors.UpdatedByRequired);

        if (guardedActions.Any(a => a.Contains(Separator)))
            return Result.Failure<TwoFactorPolicyVersion>(TwoFactorErrors.InvalidActionCode);

        // The self-protecting action is added whether or not the caller included it, so a request that omits
        // it cannot switch it off. Silently forcing it in is deliberate: the alternative is refusing the save,
        // which would make an ordinary settings screen fail for a checkbox it should not have been offering.
        var normalized = guardedActions
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Append(SelfProtectingAction)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return Result.Success(new TwoFactorPolicyVersion(
            Guid.CreateVersion7(),
            string.Join(Separator, normalized),
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            updatedBy.Trim(),
            updatedAt));
    }
}
