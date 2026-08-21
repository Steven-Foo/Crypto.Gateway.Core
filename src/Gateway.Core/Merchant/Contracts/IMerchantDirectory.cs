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
    bool CanTransact,
    int SettlementDelayDays = 0);

public interface IMerchantDirectory
{
    Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default);

    /// <summary>Batch id→Name resolve, for a caller (e.g. an Ops transaction-search screen) that already has a
    /// page of records carrying a bare <c>MerchantId</c> and needs the display name without one round-trip per
    /// row. Ids with no matching merchant are simply absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyList<Guid> merchantIds, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive "contains" search over Name and MerchantCode — the free-text merchant search
    /// box on an Ops screen, not an exact lookup. Returns matching ids only; the caller folds them into its own
    /// filter (e.g. narrowing a transaction search to "any of these merchants").</summary>
    Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string nameContains, CancellationToken cancellationToken = default);
}
