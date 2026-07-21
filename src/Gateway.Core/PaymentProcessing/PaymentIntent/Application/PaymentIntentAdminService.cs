using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;

/// <summary>Staff-initiated cancellation of a still-unpaid invoice (e.g. a test transaction).</summary>
public sealed record FailPaymentIntentCommand(Guid PublicReference, string Reason);

public interface IPaymentIntentAdminService
{
    Task<Result> FailAsync(FailPaymentIntentCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// The staff-only counterpart to <see cref="PaymentIntentService"/>. Deliberately a separate, smaller class:
/// it needs only the repository and the wallet reservation lock, not <c>IDepositAddressProvisioner</c> or
/// <c>IMerchantFeeSchedule</c> — so a host exposing only this (e.g. OperationsApi) doesn't have to compose
/// creation's dependency graph just to let staff cancel a test invoice.
/// </summary>
public sealed class PaymentIntentAdminService(
    IPaymentIntentRepository repository,
    IWalletReservationLock walletLock,
    TimeProvider timeProvider,
    ILogger<PaymentIntentAdminService> logger) : IPaymentIntentAdminService
{
    public async Task<Result> FailAsync(FailPaymentIntentCommand command, CancellationToken cancellationToken = default)
    {
        var intent = await repository.FindByPublicReferenceAsync(command.PublicReference, cancellationToken);
        if (intent is null)
            return Result.Failure(PaymentIntentErrors.NotFound);

        var walletId = intent.WalletId;
        var result = intent.Fail(command.Reason, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        await walletLock.ReleaseAsync(walletId, cancellationToken);

        logger.LogInformation("Payment intent {Reference} manually failed: {Reason}", command.PublicReference, command.Reason);
        return Result.Success();
    }
}
