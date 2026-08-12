using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Drives Pending energy operations (stake/delegate) through build → sign → broadcast, mirroring the sweep/
/// withdrawal money-out path. The private key never enters this service — it hands an unsigned blob plus the
/// staking wallet's key <em>reference</em> (resolved via <see cref="IPlatformSigningKeyDirectory"/> for
/// <see cref="DerivationPurpose.Energy"/>) to <see cref="ISigner"/> and gets back a signed blob (§10).
/// Crash-safe: the signed blob is persisted before broadcast, so a resumed operation re-broadcasts the SAME
/// transaction. A pre-broadcast failure just marks it Failed — there is nothing to release (no ledger, §15.4).
/// </summary>
public sealed class EnergyOperationProcessingService(
    IEnergyOperationRepository repository,
    IResourceOperationBuilder builder,
    ISigner signer,
    ITransactionBroadcaster broadcaster,
    IPlatformSigningKeyDirectory signingKeys,
    TimeProvider timeProvider,
    ILogger<EnergyOperationProcessingService> logger)
{
    private static readonly EnergyOperationStatus[] Processable = [EnergyOperationStatus.Pending, EnergyOperationStatus.Signing];

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetByStatusesAsync(Processable, cancellationToken);
        var processed = 0;

        foreach (var operation in pending)
        {
            if (await ProcessOneAsync(operation, cancellationToken))
                processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(EnergyOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            // Already signed (resumed pass): re-broadcast the SAME persisted blob — never rebuild.
            if (operation.Status == EnergyOperationStatus.Signing && operation.HasSignedTransaction)
                return await BroadcastAsync(operation, operation.SignedTransaction!, cancellationToken);

            var signingKey = await signingKeys.FindActiveAsync(operation.Chain, DerivationPurpose.Energy, cancellationToken);
            if (signingKey is null)
                return await FailAsync(operation, "no staking signing key registered", cancellationToken);

            var unsigned = operation.Kind == EnergyOperationKind.Stake
                ? await builder.BuildStakeForEnergyAsync(
                    new StakeForEnergyRequest(operation.Chain, operation.OwnerAddress, operation.AmountSun), cancellationToken)
                : await builder.BuildDelegateEnergyAsync(
                    new DelegateEnergyRequest(operation.Chain, operation.OwnerAddress, operation.TargetAddress!, operation.AmountSun), cancellationToken);

            var signed = await signer.SignAsync(
                new SigningRequest(operation.Id, operation.Chain, unsigned.Payload, signingKey.KeyReference), cancellationToken);
            if (signed.IsFailure)
                return await FailAsync(operation, $"sign: {signed.Error!.Message}", cancellationToken);

            var recorded = operation.RecordSigned(Guid.CreateVersion7(), signed.Value.SignedPayload, timeProvider.GetUtcNow());
            if (recorded.IsFailure)
                return false;
            await repository.SaveChangesAsync(cancellationToken);

            return await BroadcastAsync(operation, signed.Value.SignedPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing energy operation {OperationId} ({Kind}) failed; will retry next pass.", operation.Id, operation.Kind);
            return false;
        }
    }

    private async Task<bool> BroadcastAsync(EnergyOperation operation, byte[] signedPayload, CancellationToken cancellationToken)
    {
        var broadcast = await broadcaster.BroadcastAsync(operation.Chain, signedPayload, cancellationToken);
        if (broadcast.IsFailure)
            return await FailAsync(operation, $"broadcast: {broadcast.Error!.Message}", cancellationToken);

        var marked = operation.MarkBroadcast(broadcast.Value.TransactionHash, timeProvider.GetUtcNow());
        if (marked.IsFailure)
            return false;
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FailAsync(EnergyOperation operation, string reason, CancellationToken cancellationToken)
    {
        if (operation.Fail(reason, timeProvider.GetUtcNow()).IsSuccess)
        {
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }
}
