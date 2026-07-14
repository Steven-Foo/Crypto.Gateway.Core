using System.Data;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

public sealed class HdWalletRepository(KeyManagementDbContext context) : IHdWalletRepository
{
    public Task<HdWallet?> FindActiveAsync(
        Chain chain,
        HdWalletPurpose purpose,
        CancellationToken cancellationToken = default) =>
        context.HdWallets.SingleOrDefaultAsync(
            w => w.Chain == chain && w.Purpose == purpose && w.Status == HdWalletStatus.Active,
            cancellationToken);

    public Task<HdWallet?> FindByIdAsync(Guid hdWalletId, CancellationToken cancellationToken = default) =>
        context.HdWallets.SingleOrDefaultAsync(w => w.Id == hdWalletId, cancellationToken);

    public Task<DerivedKey?> FindDerivedKeyAsync(Guid derivedKeyId, CancellationToken cancellationToken = default) =>
        context.DerivedKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == derivedKeyId, cancellationToken);

    public void Add(HdWallet hdWallet) => context.HdWallets.Add(hdWallet);

    public void AddDerivedKey(DerivedKey derivedKey) => context.DerivedKeys.Add(derivedKey);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// One statement. The row is locked for the duration of the enclosing transaction, so concurrent
    /// callers queue rather than racing, and each receives a distinct index.
    ///
    /// A read-modify-write (load entity, increment, SaveChanges) would be wrong even with a
    /// rowversion: it turns a guaranteed serialisation into a retry loop, and every retry is another
    /// chance for a bug to hand out a duplicate. The whole safety property is "never the same index
    /// twice", so it is expressed as a single atomic UPDATE.
    ///
    /// The WHERE clause also refuses to allocate past 2^31-1, where BIP-32 would reinterpret the
    /// index as hardened and derive an entirely different key.
    /// </summary>
    public async Task<Result<long>> AllocateNextIndexAsync(
        Guid hdWalletId,
        CancellationToken cancellationToken = default)
    {
        // Issued as a raw ADO command rather than EF's SqlQuery: an interpolated SqlQuery would
        // parameterize the schema name, and EF may wrap a composable query in a subselect — neither
        // of which an UPDATE ... OUTPUT survives. The statement executed is exactly the one written here.
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await context.Database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            $"""
             UPDATE {KeyManagementDbContext.SchemaName}.HdWallet
             SET NextDerivationIndex = NextDerivationIndex + 1
             OUTPUT deleted.NextDerivationIndex
             WHERE Id = @hdWalletId
               AND Status = 'Active'
               AND NextDerivationIndex <= @maxIndex
             """;

        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "@hdWalletId";
        idParameter.Value = hdWalletId;
        command.Parameters.Add(idParameter);

        var maxParameter = command.CreateParameter();
        maxParameter.ParameterName = "@maxIndex";
        maxParameter.Value = DerivationPath.MaxIndex;
        command.Parameters.Add(maxParameter);

        var allocated = await command.ExecuteScalarAsync(cancellationToken);

        if (allocated is not null && allocated != DBNull.Value)
            return Result.Success(Convert.ToInt64(allocated));

        // Nothing was updated. Distinguish "gone/disabled" from "index space spent" so the caller
        // gets an actionable error rather than a generic failure.
        var wallet = await context.HdWallets.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == hdWalletId, cancellationToken);

        return wallet switch
        {
            null => Result.Failure<long>(KeyManagementErrors.NotFound),
            { Status: not HdWalletStatus.Active } => Result.Failure<long>(KeyManagementErrors.NotActive),
            _ => Result.Failure<long>(KeyManagementErrors.PoolExhausted),
        };
    }

    public async Task<Result<T>> InTransactionAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var result = await operation(ct);

            if (result.IsFailure)
            {
                // Rolling back un-consumes the index. The next caller receives it, and no address
                // was ever handed out for it — so we get neither a gap nor a reuse.
                await transaction.RollbackAsync(ct);
                return result;
            }

            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}
