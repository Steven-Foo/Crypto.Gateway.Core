using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;

/// <summary>Staff holds a wallet — e.g. an address received an unexpected/off-flow transfer and is being
/// held for investigation. See <c>Wallet.Suspend</c>.</summary>
public sealed record SuspendWalletCommand(Guid WalletId, string Reason);

/// <summary>Staff lifts a hold — restores the wallet to normal deposit-accepting service.</summary>
public sealed record ResumeWalletCommand(Guid WalletId);

public interface IWalletAdminService
{
    Task<(IReadOnlyList<WalletAdminRow> Items, int TotalCount)> SearchAsync(
        WalletAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<Result> SuspendAsync(SuspendWalletCommand command, CancellationToken cancellationToken = default);

    Task<Result> ResumeAsync(ResumeWalletCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// The staff-only counterpart to <see cref="WalletProvisioningService"/>: browsing wallets and placing/lifting
/// a temporary hold. Deliberately does not touch <c>MerchantId</c> or the wallet's assignment — that is
/// exactly what distinguishes a hold from <c>Wallet.Disable</c> (a permanent decommission).
/// </summary>
public sealed class WalletAdminService(
    IWalletRepository repository, TimeProvider timeProvider, ILogger<WalletAdminService> logger) : IWalletAdminService
{
    public Task<(IReadOnlyList<WalletAdminRow> Items, int TotalCount)> SearchAsync(
        WalletAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default) =>
        repository.SearchAsync(filter, page, pageSize, cancellationToken);

    public async Task<Result> SuspendAsync(SuspendWalletCommand command, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.GetByIdAsync(command.WalletId, cancellationToken);
        if (wallet is null)
            return Result.Failure(WalletErrors.NotFound);

        var result = wallet.Suspend(command.Reason, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Wallet {WalletId} suspended by staff: {Reason}", command.WalletId, command.Reason);
        return Result.Success();
    }

    public async Task<Result> ResumeAsync(ResumeWalletCommand command, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.GetByIdAsync(command.WalletId, cancellationToken);
        if (wallet is null)
            return Result.Failure(WalletErrors.NotFound);

        var result = wallet.Resume(timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Wallet {WalletId} resumed by staff.", command.WalletId);
        return Result.Success();
    }
}
