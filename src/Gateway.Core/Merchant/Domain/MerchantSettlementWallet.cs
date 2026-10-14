using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// Where a settlement (cash-out) address stands. Only <see cref="Active"/> receives money; <see cref="Retired"/>
/// is approved but on standby. The other three are the merchant-submission lifecycle (2026-10-14): a merchant may
/// propose an address from the merchant BO, but it can't be used until platform staff approve it.
/// </summary>
public enum SettlementWalletStatus
{
    /// <summary>The destination for its chain — approved AND in use. Exactly one per (merchant, chain) at a
    /// time, enforced by a filtered unique index rather than by application logic.</summary>
    Active = 0,

    /// <summary>Approved but on standby (已审核/备用): not in use, ready to be made active by staff or the
    /// merchant. Kept rather than deleted, so switching between known addresses needs no re-approval. (The name
    /// predates the approval flow and is kept so existing rows need no data change.)</summary>
    Retired = 1,

    /// <summary>Submitted by the merchant, waiting for platform staff (待审核). Can never receive money.</summary>
    PendingApproval = 2,

    /// <summary>Platform staff refused it (已拒绝); <see cref="MerchantSettlementWallet.RejectionReason"/> says
    /// why. The merchant may submit the same address again.</summary>
    Rejected = 3,

    /// <summary>The merchant withdrew its own request before staff reviewed it.</summary>
    Cancelled = 4,
}

/// <summary>Who put the address on file.</summary>
public enum SettlementWalletSource
{
    /// <summary>Added by platform staff in the admin BO — approved on the spot.</summary>
    Staff = 0,

    /// <summary>Submitted by the merchant from the merchant BO — needs staff approval.</summary>
    Merchant = 1,
}

/// <summary>
/// A merchant's whitelisted settlement (cash-out) address for one chain — the destination of a
/// <b>Merchant Withdrawal</b> (earnings cash-out). Pre-registered by staff so a compromised merchant API key
/// can never redirect earnings (§10): the cash-out endpoint resolves the destination from here, it is never
/// client-supplied.
///
/// <para>A merchant may have several on file per chain, but only one <see cref="SettlementWalletStatus.Active"/>
/// — the one that gets paid. Switching between them is an activate, not an edit of an address in place, so
/// the record of which address was whitelisted when survives.</para>
/// </summary>
public sealed class MerchantSettlementWallet : Entity<Guid>
{
    private MerchantSettlementWallet(
        Guid id, Guid merchantId, Chain chain, string address, string? label,
        SettlementWalletStatus status, SettlementWalletSource source, string? submittedBy,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        Chain = chain;
        Address = address;
        Label = label;
        Status = status;
        Source = source;
        SubmittedBy = submittedBy;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantSettlementWallet() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;

    /// <summary>Staff's own name for the address ("treasury desk, Ledger 2"), so two whitelisted addresses
    /// can be told apart without comparing base58 strings.</summary>
    public string? Label { get; private set; }

    public SettlementWalletStatus Status { get; private set; }

    /// <summary>Who put it on file — staff (approved on the spot) or the merchant (needs approval).</summary>
    public SettlementWalletSource Source { get; private set; }

    /// <summary>Who added or submitted it — a staff or merchant-portal username, from the session.</summary>
    public string? SubmittedBy { get; private set; }

    /// <summary>The staff member who approved or rejected a merchant submission, and when. Null for a
    /// staff-added address (no separate review) and for one still pending.</summary>
    public string? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>Why staff rejected it — shown to the merchant.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>
    /// The last AML screening decision recorded against this address (e.g. <c>Allow</c>, <c>Review</c>,
    /// <c>Block</c>) and when. Stored on the wallet so the merchant BO — which has no screening module — can
    /// still refuse to make a sanctioned address active. Null = never screened.
    /// </summary>
    public string? ScreeningDecision { get; private set; }
    public DateTimeOffset? ScreenedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == SettlementWalletStatus.Active;

    /// <summary>Approved — Active or on standby. Only an approved address can be made active.</summary>
    public bool IsApproved => Status is SettlementWalletStatus.Active or SettlementWalletStatus.Retired;

    internal static Result<MerchantSettlementWallet> Create(
        Guid merchantId, Chain chain, string address, string? label, bool activate, DateTimeOffset now,
        string? submittedBy = null)
    {
        var validated = Validate(address);
        if (validated.IsFailure)
            return Result.Failure<MerchantSettlementWallet>(validated.Error!);

        return Result.Success(new MerchantSettlementWallet(
            Guid.CreateVersion7(), merchantId, chain, validated.Value,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            activate ? SettlementWalletStatus.Active : SettlementWalletStatus.Retired,
            SettlementWalletSource.Staff, Normalize(submittedBy), now));
    }

    /// <summary>A merchant's submission from the merchant BO — starts as <see cref="SettlementWalletStatus.PendingApproval"/>.</summary>
    internal static Result<MerchantSettlementWallet> CreateRequested(
        Guid merchantId, Chain chain, string address, string? label, string submittedBy, DateTimeOffset now)
    {
        var validated = Validate(address);
        if (validated.IsFailure)
            return Result.Failure<MerchantSettlementWallet>(validated.Error!);

        return Result.Success(new MerchantSettlementWallet(
            Guid.CreateVersion7(), merchantId, chain, validated.Value,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            SettlementWalletStatus.PendingApproval, SettlementWalletSource.Merchant, Normalize(submittedBy), now));
    }

    /// <summary>The merchant submits again an address that was rejected or that it cancelled earlier.</summary>
    internal void Resubmit(string? label, string submittedBy, DateTimeOffset now)
    {
        Status = SettlementWalletStatus.PendingApproval;
        Source = SettlementWalletSource.Merchant;
        SubmittedBy = Normalize(submittedBy);
        ReviewedBy = null;
        ReviewedAt = null;
        RejectionReason = null;
        if (!string.IsNullOrWhiteSpace(label))
            Label = label.Trim();
        UpdatedAt = now;
    }

    /// <summary>Staff approve a pending submission: it goes on standby, ready to be made active.</summary>
    internal Result Approve(string reviewedBy, DateTimeOffset now)
    {
        if (Status != SettlementWalletStatus.PendingApproval)
            return Result.Failure(MerchantErrors.SettlementWalletNotPending);

        Status = SettlementWalletStatus.Retired;
        ReviewedBy = Normalize(reviewedBy);
        ReviewedAt = now;
        RejectionReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    internal Result Reject(string reviewedBy, string reason, DateTimeOffset now)
    {
        if (Status != SettlementWalletStatus.PendingApproval)
            return Result.Failure(MerchantErrors.SettlementWalletNotPending);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(MerchantErrors.SettlementWalletRejectionReasonRequired);

        Status = SettlementWalletStatus.Rejected;
        ReviewedBy = Normalize(reviewedBy);
        ReviewedAt = now;
        RejectionReason = reason.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    internal Result Cancel(DateTimeOffset now)
    {
        if (Status != SettlementWalletStatus.PendingApproval)
            return Result.Failure(MerchantErrors.SettlementWalletNotPending);

        Status = SettlementWalletStatus.Cancelled;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Staff adding an address that a merchant had already submitted (or that was rejected/cancelled)
    /// is itself the approval — staff-added addresses are approved on the spot.</summary>
    internal void ApproveByStaffAdd(string? staff, DateTimeOffset now)
    {
        if (IsApproved)
            return;

        Status = SettlementWalletStatus.Retired;
        ReviewedBy = Normalize(staff);
        ReviewedAt = now;
        RejectionReason = null;
        UpdatedAt = now;
    }

    internal void RecordScreening(string? decision, DateTimeOffset now)
    {
        if (decision is null)
            return;

        ScreeningDecision = decision;
        ScreenedAt = now;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal void Activate(DateTimeOffset now)
    {
        if (Status == SettlementWalletStatus.Active)
            return;

        Status = SettlementWalletStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>Back to standby. Only for an approved address — a pending, rejected or cancelled one is left
    /// exactly as it is.</summary>
    internal void Retire(DateTimeOffset now)
    {
        if (Status != SettlementWalletStatus.Active)
            return;

        Status = SettlementWalletStatus.Retired;
        UpdatedAt = now;
    }

    internal void Rename(string? label, DateTimeOffset now)
    {
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        UpdatedAt = now;
    }

    private static Result<string> Validate(string address) =>
        string.IsNullOrWhiteSpace(address)
            ? Result.Failure<string>(MerchantErrors.SettlementAddressRequired)
            : Result.Success(address.Trim());
}
