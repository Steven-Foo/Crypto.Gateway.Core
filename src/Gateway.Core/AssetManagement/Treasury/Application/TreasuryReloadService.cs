using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>The unsigned reload for the operator to sign: its id and the unsigned transaction (hex) to sign
/// client-side. The key never reaches the backend; the operator posts back only the signed blob (§10).</summary>
public sealed record InitiatedReload(Guid ReloadId, string UnsignedTransactionHex);

public interface ITreasuryReloadService
{
    /// <summary>Builds the unsigned treasury→hot transfer for the operator to sign (target hot wallet chosen by
    /// the operator). Stores it as <c>AwaitingSignature</c> and returns the unsigned transaction to sign.</summary>
    Task<Result<InitiatedReload>> InitiateAsync(
        Chain chain, Guid assetId, Guid targetWalletId, BigInteger amount, CancellationToken cancellationToken = default);

    /// <summary>Accepts the operator's client-side-signed blob for a reload; a worker then broadcasts it.</summary>
    Task<Result> SubmitSignedAsync(Guid reloadId, byte[] signedTransaction, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the treasury cold-reload's human-in-the-loop start: builds the unsigned treasury→hot transfer
/// (<see cref="ITransactionBuilder"/>) FROM the cold treasury address TO an operator-chosen hot-pool wallet, and
/// records the operator's signed blob. It never holds a key — the cold key signs client-side (§10) — and never
/// touches the ledger: the reload moves funds between two platform-controlled addresses (§14).
/// </summary>
public sealed class TreasuryReloadService(
    ITreasuryColdWalletDirectory coldWallets,
    ITreasuryHotWalletDirectory hotWallets,
    ITransactionBuilder transactionBuilder,
    ITreasuryReloadRepository repository,
    TimeProvider timeProvider) : ITreasuryReloadService
{
    public async Task<Result<InitiatedReload>> InitiateAsync(
        Chain chain, Guid assetId, Guid targetWalletId, BigInteger amount, CancellationToken cancellationToken = default)
    {
        if (amount <= BigInteger.Zero)
            return Result.Failure<InitiatedReload>(TreasuryReloadErrors.AmountNotPositive);

        var cold = await coldWallets.GetAsync(chain, cancellationToken);
        if (cold.IsFailure)
            return Result.Failure<InitiatedReload>(cold.Error!);

        // The operator chose which hot-pool wallet to top up — resolve its address from the pool.
        var pool = await hotWallets.GetHotWalletPoolAsync(chain, cancellationToken);
        var target = pool.FirstOrDefault(w => w.WalletId == targetWalletId);
        if (target is null)
            return Result.Failure<InitiatedReload>(TreasuryReloadErrors.TargetWalletRequired);

        var unsigned = await transactionBuilder.BuildTransferAsync(
            new BuildWithdrawalRequest(chain, assetId, cold.Value.Address, target.Address, amount), cancellationToken);

        var reload = TreasuryReload.Initiate(
            chain, assetId, cold.Value.Address, targetWalletId, target.Address, amount, unsigned.Payload, timeProvider.GetUtcNow());
        if (reload.IsFailure)
            return Result.Failure<InitiatedReload>(reload.Error!);

        await repository.AddAsync(reload.Value, cancellationToken);
        return Result.Success(new InitiatedReload(reload.Value.Id, Convert.ToHexString(unsigned.Payload)));
    }

    public async Task<Result> SubmitSignedAsync(Guid reloadId, byte[] signedTransaction, CancellationToken cancellationToken = default)
    {
        var reload = await repository.GetByIdAsync(reloadId, cancellationToken);
        if (reload is null)
            return Result.Failure(TreasuryReloadErrors.NotFound);

        var result = reload.SubmitSigned(signedTransaction, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
