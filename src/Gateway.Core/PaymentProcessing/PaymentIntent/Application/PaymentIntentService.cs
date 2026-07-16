using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;

/// <summary>
/// Create a deposit invoice. <see cref="ReceiveAmount"/> is what the merchant wants to <em>net</em> (unsigned
/// base units); with payer-on-top pricing the invoice asks the payer for that amount grossed up by the deposit
/// fee, so the merchant is credited their target and the platform earns the fee. The host converts from
/// display at the edge (§14).
/// </summary>
public sealed record CreatePaymentIntentCommand(
    Guid MerchantId,
    string MerchantTransactionId,
    Chain Chain,
    Guid AssetId,
    BigInteger ReceiveAmount,
    string? CallbackUrl);

/// <summary>What the merchant gets back: the public reference for the pay URL, the address, and when it lapses.</summary>
public sealed record PaymentIntentResult(Guid Reference, string Address, Chain Chain, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public interface IPaymentIntentService
{
    Task<Result<PaymentIntentResult>> CreateAsync(CreatePaymentIntentCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates invoices and assigns each a deposit address, reusing a free one from the merchant's pool before
/// minting a new one — the concentration strategy that keeps address count (and therefore sweep gas) low.
/// No distributed lock: the DB unique indexes are the arbiter (§7.3). A lost address race retries with a
/// fresh address; a duplicate reference returns the winner. Application holds no infrastructure (§4.4).
/// </summary>
public sealed class PaymentIntentService(
    IPaymentIntentRepository repository,
    IDepositAddressProvisioner addressProvisioner,
    IMerchantFeeSchedule feeSchedule,
    IOptions<PaymentIntentOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentIntentService> logger) : IPaymentIntentService
{
    private readonly PaymentIntentOptions _options = options.Value;

    public async Task<Result<PaymentIntentResult>> CreateAsync(
        CreatePaymentIntentCommand command, CancellationToken cancellationToken = default)
    {
        // 1. Idempotent replay: same merchant reference → return the existing invoice unchanged.
        var existing = await repository.FindByMerchantReferenceAsync(command.MerchantId, command.MerchantTransactionId, cancellationToken);
        if (existing is not null)
            return Result.Success(ToResult(existing));

        // 2. Payer-on-top: ask the payer for the merchant's target net grossed up by the deposit fee, so the
        //    Ledger's fee split leaves the merchant with (at least) what they asked to receive. No fee → gross
        //    equals the requested amount (unpriced merchants are unaffected). Deterministic across retries.
        var grossResult = await feeSchedule.GrossUpDepositAsync(command.MerchantId, command.AssetId, command.ReceiveAmount, cancellationToken);
        if (grossResult.IsFailure)
            return Result.Failure<PaymentIntentResult>(grossResult.Error!);

        var expectedAmount = grossResult.Value;

        // 3. Reserve an address and insert. First attempt reuses a free address; retries mint a fresh one.
        for (var attempt = 0; attempt < _options.MaxProvisionRetries; attempt++)
        {
            var now = timeProvider.GetUtcNow();

            var address = await AcquireAddressAsync(command.MerchantId, command.Chain, forceNew: attempt > 0, cancellationToken);
            if (address.IsFailure)
                return Result.Failure<PaymentIntentResult>(address.Error!);

            var intentResult = PaymentIntentEntity.Create(
                command.MerchantId, command.MerchantTransactionId, command.Chain, command.AssetId,
                address.Value.WalletId, address.Value.Address, expectedAmount, command.CallbackUrl,
                now.AddMinutes(_options.ExpiryMinutes), now);

            if (intentResult.IsFailure)
                return Result.Failure<PaymentIntentResult>(intentResult.Error!);

            var outcome = await repository.TryAddAsync(intentResult.Value, cancellationToken);
            switch (outcome)
            {
                case PaymentIntentAddOutcome.Added:
                    return Result.Success(ToResult(intentResult.Value));

                case PaymentIntentAddOutcome.DuplicateReference:
                    var winner = await repository.FindByMerchantReferenceAsync(command.MerchantId, command.MerchantTransactionId, cancellationToken);
                    return winner is not null
                        ? Result.Success(ToResult(winner))
                        : Result.Failure<PaymentIntentResult>(PaymentIntentErrors.DuplicateReference);

                case PaymentIntentAddOutcome.AddressBusy:
                    logger.LogDebug("Address {WalletId} taken concurrently; retrying with a fresh address.", address.Value.WalletId);
                    continue;
            }
        }

        return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.AddressUnavailable);
    }

    private async Task<Result<ReusableAddress>> AcquireAddressAsync(Guid merchantId, Chain chain, bool forceNew, CancellationToken cancellationToken)
    {
        if (!forceNew)
        {
            var reusable = await repository.FindReusableAddressAsync(merchantId, chain, cancellationToken);
            if (reusable is not null)
                return Result.Success(reusable);
        }

        var provisioned = await addressProvisioner.ProvisionDepositAddressAsync(merchantId, chain, cancellationToken);
        return provisioned.IsFailure
            ? Result.Failure<ReusableAddress>(provisioned.Error!)
            : Result.Success(new ReusableAddress(provisioned.Value.WalletId, provisioned.Value.Address));
    }

    private static PaymentIntentResult ToResult(PaymentIntentEntity intent) =>
        new(intent.PublicReference, intent.Address, intent.Chain, intent.ExpiresAt, intent.CreatedAt);
}
