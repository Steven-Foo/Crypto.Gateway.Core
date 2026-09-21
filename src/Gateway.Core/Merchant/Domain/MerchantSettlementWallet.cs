using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>Whether a whitelisted settlement wallet is the one a chain's cash-outs are paid to.</summary>
public enum SettlementWalletStatus
{
    /// <summary>The destination for its chain. Exactly one per (merchant, chain) at a time, enforced by a
    /// filtered unique index rather than by application logic.</summary>
    Active = 0,

    /// <summary>On file but not in use. Kept rather than deleted: a merchant switching between two known
    /// addresses should not require staff to re-whitelist (and re-screen) one they approved before.</summary>
    Retired = 1,
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
        SettlementWalletStatus status, DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        Chain = chain;
        Address = address;
        Label = label;
        Status = status;
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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == SettlementWalletStatus.Active;

    internal static Result<MerchantSettlementWallet> Create(
        Guid merchantId, Chain chain, string address, string? label, bool activate, DateTimeOffset now)
    {
        var validated = Validate(address);
        if (validated.IsFailure)
            return Result.Failure<MerchantSettlementWallet>(validated.Error!);

        return Result.Success(new MerchantSettlementWallet(
            Guid.CreateVersion7(), merchantId, chain, validated.Value,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            activate ? SettlementWalletStatus.Active : SettlementWalletStatus.Retired, now));
    }

    internal void Activate(DateTimeOffset now)
    {
        if (Status == SettlementWalletStatus.Active)
            return;

        Status = SettlementWalletStatus.Active;
        UpdatedAt = now;
    }

    internal void Retire(DateTimeOffset now)
    {
        if (Status == SettlementWalletStatus.Retired)
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
