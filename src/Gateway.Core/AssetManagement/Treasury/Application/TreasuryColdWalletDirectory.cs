using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>Resolves cold collection wallets for a chain from Treasury's own persistence.</summary>
public sealed class TreasuryColdWalletDirectory(ITreasuryColdWalletRepository repository) : ITreasuryColdWalletDirectory
{
    public async Task<Result<ColdTreasuryWallet>> GetAsync(
        Chain chain, ColdWalletKind kind, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.FindActiveAsync(chain, kind, cancellationToken);
        return wallet is null
            ? Result.Failure<ColdTreasuryWallet>(TreasuryColdWalletErrors.NotConfigured)
            : Result.Success(new ColdTreasuryWallet(wallet.Id, chain, kind, wallet.Address));
    }

    public async Task<IReadOnlyList<RegisteredColdTreasuryWallet>> ListAsync(CancellationToken cancellationToken = default)
    {
        var wallets = await repository.ListAsync(cancellationToken);
        return wallets
            .OrderBy(w => w.Chain)
            .ThenBy(w => w.Kind)
            // Active first within a kind, then newest: the question a staff screen opens with is "where are
            // sweeps going right now", and the answer should never be somewhere down a list of retired rows.
            .ThenByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .Select(Project)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListCustodyAddressesAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        var wallets = await repository.ListForChainAsync(chain, cancellationToken);

        // Every kind and every status: what the platform holds, not what it is currently sweeping into.
        return wallets.Select(w => w.Address).Distinct(StringComparer.Ordinal).ToList();
    }

    internal static RegisteredColdTreasuryWallet Project(TreasuryColdWallet w) => new(
        w.Id, w.Chain, w.Kind, w.Address, w.Label, w.Status,
        w.ScreeningDecision, w.ScreeningScore, w.ScreeningId, w.ScreenedAt, w.CreatedAt, w.UpdatedAt);
}
