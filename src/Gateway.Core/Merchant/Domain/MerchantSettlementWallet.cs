using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// A merchant's whitelisted settlement (cash-out) address for one chain — the fixed destination of a
/// <b>Merchant Withdrawal</b> (earnings cash-out). Pre-registered by staff so a compromised merchant API key
/// can never redirect earnings to an attacker (§10): the cash-out endpoint resolves the destination from
/// here, it is never client-supplied. One per <c>(MerchantId, Chain)</c>.
/// </summary>
public sealed class MerchantSettlementWallet : Entity<Guid>
{
    private MerchantSettlementWallet(Guid id, Guid merchantId, Chain chain, string address, DateTimeOffset createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        Chain = chain;
        Address = address;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantSettlementWallet() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static Result<MerchantSettlementWallet> Create(Guid merchantId, Chain chain, string address, DateTimeOffset now)
    {
        var validated = Validate(address);
        if (validated.IsFailure)
            return Result.Failure<MerchantSettlementWallet>(validated.Error!);

        return Result.Success(new MerchantSettlementWallet(Guid.CreateVersion7(), merchantId, chain, validated.Value, now));
    }

    internal Result Update(string address, DateTimeOffset now)
    {
        var validated = Validate(address);
        if (validated.IsFailure)
            return validated;

        Address = validated.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result<string> Validate(string address) =>
        string.IsNullOrWhiteSpace(address)
            ? Result.Failure<string>(MerchantErrors.SettlementAddressRequired)
            : Result.Success(address.Trim());
}
