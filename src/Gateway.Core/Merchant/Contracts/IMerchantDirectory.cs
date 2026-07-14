namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// The Merchant module's public read model. Other modules (Deposit, Withdrawal, Ledger) depend on
/// this shape and nothing else — never on the Merchant aggregate, its DbContext, or its tables.
/// Deliberately carries no credential material.
/// </summary>
public sealed record MerchantSummary(
    Guid MerchantId,
    string MerchantCode,
    string Name,
    string? CallbackUrl,
    bool CanTransact);

public interface IMerchantDirectory
{
    Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default);
}
