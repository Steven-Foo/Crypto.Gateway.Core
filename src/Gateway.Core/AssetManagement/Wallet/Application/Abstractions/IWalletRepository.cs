using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;

using WalletEntity = Domain.Wallet;

/// <summary>Ops search filter — every field is an optional narrowing AND. <see cref="Address"/> is an exact
/// match (staff copy/paste the address from a transaction record, not a partial search).</summary>
public sealed record WalletAdminFilter(
    Guid? MerchantId,
    string? Address,
    Chain? Chain,
    WalletStatus? Status);

/// <summary>The Ops wallet-search read row.</summary>
public sealed record WalletAdminRow(
    Guid WalletId,
    Guid? MerchantId,
    Chain Chain,
    string Address,
    string WalletType,
    string Status,
    string? StatusReason,
    int DepositsReceivedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IWalletRepository
{
    Task<WalletEntity?> GetByIdAsync(Guid walletId, CancellationToken cancellationToken = default);

    Task<WalletEntity?> GetByDerivedKeyIdAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);

    Task<WalletEntity?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    void Add(WalletEntity wallet);

    /// <summary>
    /// Inserts a wallet, saving immediately. Returns <c>false</c> when the unique <c>(Chain, Address)</c>
    /// index rejects it — a concurrent registration for the same address won — so the caller adopts the
    /// existing row instead of failing. Keeps the EF-specific race translation inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TryAddAsync(WalletEntity wallet, CancellationToken cancellationToken = default);

    /// <summary>Ops search/browse — every filter field is optional. <paramref name="page"/> is 1-based.</summary>
    Task<(IReadOnlyList<WalletAdminRow> Items, int TotalCount)> SearchAsync(
        WalletAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
