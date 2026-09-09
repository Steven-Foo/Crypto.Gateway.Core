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
/// Create a deposit invoice. <see cref="InvoiceAmount"/> is the amount the invoice <em>asks the payer to
/// send</em> (unsigned base units) — exactly what they are shown, never inflated. The platform fee is then
/// deducted from what arrives, so the merchant is credited <c>amount − fee</c>. The host converts from
/// display at the edge (§14).
///
/// <para><see cref="Kind"/> distinguishes a customer payment from the merchant funding its own balance. It is
/// declared here, at creation, because on-chain the two are indistinguishable. It selects which fee rate
/// prices the arriving deposit and whether the credit is held for the merchant's T+N settlement period — it
/// does <b>not</b> change the amount asked for: both kinds send precisely the figure quoted.</para>
/// </summary>
public sealed record CreatePaymentIntentCommand(
    Guid MerchantId,
    string MerchantTransactionId,
    Chain Chain,
    Guid AssetId,
    BigInteger InvoiceAmount,
    string? CallbackUrl,
    PaymentIntentKind Kind = PaymentIntentKind.Customer);

/// <summary>What the merchant gets back: the public reference for the pay URL, the address, and when it lapses.</summary>
public sealed record PaymentIntentResult(Guid Reference, string Address, Chain Chain, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public interface IPaymentIntentService
{
    Task<Result<PaymentIntentResult>> CreateAsync(CreatePaymentIntentCommand command, CancellationToken cancellationToken = default);
}

/// <summary>A wallet address available to hold this invoice, reused or freshly minted.</summary>
public sealed record ReusableAddress(Guid WalletId, string Address);

/// <summary>
/// Creates invoices and assigns each a deposit address, reusing the merchant's busiest wallet first — the
/// concentration strategy that keeps address count (and therefore sweep gas) low. Address candidates come
/// from the Wallet module's directory (Contracts-only, §4.5), sorted by deposit activity descending; each is
/// tried non-blocking against <see cref="IWalletReservationLock"/> until one is free, so a request never mints
/// a new wallet just because the busiest one happened to be reserved a moment earlier. The reservation's TTL
/// spans the invoice's full Waiting + grace window, so it needs no separate extend step — it lapses on its
/// own exactly when the invoice would anyway. The DB's filtered UNIQUE index on the live wallet remains the
/// money-safety backstop if the reservation is ever lost (§7.3) — that degrades to "mint an extra wallet,"
/// never to two invoices sharing an address.
/// </summary>
public sealed class PaymentIntentService(
    IPaymentIntentRepository repository,
    IWalletDirectory walletDirectory,
    IWalletReservationLock walletLock,
    IDepositAddressProvisioner addressProvisioner,
    IMerchantDepositLimits depositLimits,
    IPlatformDepositFloor platformDepositFloor,
    IOptions<PaymentIntentOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentIntentService> logger) : IPaymentIntentService
{
    private readonly PaymentIntentOptions _options = options.Value;

    public async Task<Result<PaymentIntentResult>> CreateAsync(
        CreatePaymentIntentCommand command, CancellationToken cancellationToken = default)
    {
        // 1. Reject a reused merchant reference outright — the merchant integration must send a unique
        //    transactionId per deposit. This intentionally trades away retry-safe idempotent replay (§7.3's
        //    usual pattern) for an explicit error, per product decision: a client-side retry with the same
        //    transactionId now surfaces as a hard duplicate rather than silently returning the prior invoice.
        var existing = await repository.FindByMerchantReferenceAsync(command.MerchantId, command.MerchantTransactionId, cancellationToken);
        if (existing is not null)
            return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.DuplicateReference);

        // 2. The invoice asks for EXACTLY the amount requested — the payer is never quietly asked for more.
        //    The platform fee is then deducted from what arrives, so the merchant is credited amount − fee and
        //    the fee becomes revenue. This holds for BOTH kinds: a customer paying an invoice and a merchant
        //    topping up its own balance send precisely the figure they were shown.
        //
        //    This replaced payer-on-top gross-up (where the invoice was inflated so the merchant netted its
        //    target and the payer silently covered the fee). The deciding reason is that a payer must never be
        //    asked to pay more than the invoice they agreed to; the merchant reconciles from the amount we
        //    record against each deposit, which is exactly what arrived.
        var expectedAmount = command.InvoiceAmount;

        // Deposit (payin) min/max: a crypto deposit can't be rejected once it's already on-chain, so this is
        // the only point that can actually gate it — before the payer ever gets an address to send to.
        // Merchant override (null bound) falls back to the platform's per-chain dust-floor config for the
        // minimum; there is no platform-wide default maximum today, so an unset merchant max stays unbounded.
        var limits = await depositLimits.GetAsync(command.MerchantId, command.AssetId, cancellationToken);
        var effectiveMinimum = limits.Minimum ?? platformDepositFloor.For(command.Chain);
        if (expectedAmount < effectiveMinimum)
            return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.BelowMinimum);
        if (limits.Maximum is { } maximum && expectedAmount > maximum)
            return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.AboveMaximum);

        var reservationTtl = TimeSpan.FromMinutes(_options.ExpiryMinutes + _options.GraceMinutes);

        // 3. Reserve an address and insert. First attempt reuses a free address; retries mint a fresh one —
        //    only reachable now via the rare DB-level AddressBusy backstop, since the reservation lock
        //    already prevented the ordinary concurrent-request race before we ever get here.
        for (var attempt = 0; attempt < _options.MaxProvisionRetries; attempt++)
        {
            var now = timeProvider.GetUtcNow();

            var address = await AcquireAddressAsync(
                command.MerchantId, command.Chain, command.MerchantTransactionId, reservationTtl, forceNew: attempt > 0, cancellationToken);
            if (address.IsFailure)
                return Result.Failure<PaymentIntentResult>(address.Error!);

            var intentResult = PaymentIntentEntity.Create(
                command.MerchantId, command.MerchantTransactionId, command.Chain, command.AssetId,
                address.Value.WalletId, address.Value.Address, expectedAmount, command.CallbackUrl,
                now.AddMinutes(_options.ExpiryMinutes), now.AddMinutes(_options.ExpiryMinutes + _options.GraceMinutes), now,
                command.Kind);

            if (intentResult.IsFailure)
            {
                await walletLock.ReleaseAsync(address.Value.WalletId, cancellationToken);
                return Result.Failure<PaymentIntentResult>(intentResult.Error!);
            }

            var outcome = await repository.TryAddAsync(intentResult.Value, cancellationToken);
            switch (outcome)
            {
                case PaymentIntentAddOutcome.Added:
                    return Result.Success(ToResult(intentResult.Value));

                case PaymentIntentAddOutcome.DuplicateReference:
                    // Lost a concurrent race against another request with the same transactionId — same
                    // hard-duplicate error as the upfront check above, not a replay.
                    await walletLock.ReleaseAsync(address.Value.WalletId, cancellationToken); // reserved for nothing — give it back
                    return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.DuplicateReference);

                case PaymentIntentAddOutcome.AddressBusy:
                    // Should be near-impossible with the reservation lock in place — a race the lock missed
                    // (e.g. a lost/expired reservation). Give up this wallet and mint a fresh one.
                    await walletLock.ReleaseAsync(address.Value.WalletId, cancellationToken);
                    logger.LogWarning("Address {WalletId} was busy despite a held reservation; minting a fresh one.", address.Value.WalletId);
                    continue;
            }
        }

        return Result.Failure<PaymentIntentResult>(PaymentIntentErrors.AddressUnavailable);
    }

    private async Task<Result<ReusableAddress>> AcquireAddressAsync(
        Guid merchantId, Chain chain, string referenceId, TimeSpan reservationTtl, bool forceNew, CancellationToken cancellationToken)
    {
        if (!forceNew)
        {
            // Busiest-first: the wallet closest to a sweep threshold gets reused before an idle one.
            var candidates = await walletDirectory.ListAssignedWalletsAsync(merchantId, chain, cancellationToken);
            foreach (var candidate in candidates)
            {
                if (await walletLock.TryReserveAsync(candidate.WalletId, referenceId, reservationTtl, cancellationToken))
                    return Result.Success(new ReusableAddress(candidate.WalletId, candidate.Address));
            }
        }

        // Pool exhausted (every candidate already reserved) or a forced retry — mint a new one. It has never
        // been reserved by anyone, so the claim below is expected to always succeed.
        var provisioned = await addressProvisioner.ProvisionDepositAddressAsync(merchantId, chain, cancellationToken);
        if (provisioned.IsFailure)
            return Result.Failure<ReusableAddress>(provisioned.Error!);

        await walletLock.TryReserveAsync(provisioned.Value.WalletId, referenceId, reservationTtl, cancellationToken);
        return Result.Success(new ReusableAddress(provisioned.Value.WalletId, provisioned.Value.Address));
    }

    private static PaymentIntentResult ToResult(PaymentIntentEntity intent) =>
        new(intent.PublicReference, intent.Address, intent.Chain, intent.ExpiresAt, intent.CreatedAt);
}
