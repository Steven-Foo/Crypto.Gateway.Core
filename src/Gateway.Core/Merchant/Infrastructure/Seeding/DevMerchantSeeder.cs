using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Seeding;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Idempotently creates one active test merchant with the fixed credentials in
/// <see cref="DevMerchantSeedOptions"/>, so a signed <c>/api/v1</c> request works on a fresh clone. It uses
/// the same hasher + cipher as real registration, so the seeded credential is indistinguishable to the
/// verifier — only the inputs are fixed instead of random, and the merchant is activated immediately.
///
/// The host registers this only in the Development branch. It never runs in production, where merchants are
/// onboarded through the real registrar.
/// </summary>
public sealed class DevMerchantSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DevMerchantSeedOptions> options,
    IApiSecretHasher hasher,
    ISecretCipher secretCipher,
    TimeProvider timeProvider,
    ILogger<DevMerchantSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (!seed.Enabled)
            return;

        // Fail loud but non-fatal: without these, every signed request would 401 with no obvious cause.
        if (string.IsNullOrWhiteSpace(seed.ApiKey) || string.IsNullOrWhiteSpace(seed.SigningSecret))
        {
            logger.LogWarning("Dev merchant seed is enabled but ApiKey/SigningSecret are missing — skipping.");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMerchantRepository>();

            var now = timeProvider.GetUtcNow();
            var normalisedCode = seed.MerchantCode.Trim().ToUpperInvariant();

            // Load tracked (with its policies) so a re-seed re-prices idempotently instead of bailing early.
            var merchant = await repository.GetByCodeAsync(normalisedCode, cancellationToken);
            var created = merchant is null;

            if (merchant is null)
            {
                var merchantResult = Domain.Merchant.Create(seed.MerchantCode, seed.Name, seed.CallbackUrl, timeProvider);
                if (merchantResult.IsFailure)
                {
                    logger.LogWarning("Dev merchant seed skipped: {Error}.", merchantResult.Error!.Message);
                    return;
                }

                merchant = merchantResult.Value;
                merchant.Activate(now); // dev: transactable immediately, so a signed request passes the CanTransact gate

                var secretHash = hasher.Hash(seed.ApiSecret);
                var signingSecretCipher = secretCipher.Protect(seed.SigningSecret);

                var issueResult = merchant.IssueCredential(
                    seed.ApiKey, secretHash, hasher.CurrentVersion, signingSecretCipher, now);
                if (issueResult.IsFailure)
                {
                    logger.LogWarning("Dev merchant seed skipped: {Error}.", issueResult.Error!.Message);
                    return;
                }

                repository.Add(merchant);
            }

            // DEV sample pricing so the round-trip shows a NON-ZERO fee. Idempotent upsert, applied whether the
            // merchant was just created or already existed. A no-op when no sample bps are configured, or when no
            // asset catalog is composed (e.g. a Merchant-only test host — resolved softly, never a hard dep).
            await ApplySampleFeesAsync(scope, merchant, seed, now, cancellationToken);

            await repository.SaveChangesAsync(cancellationToken);

            // The API key is a public identifier — safe to log. The signing secret is NOT logged (§10).
            if (created)
                logger.LogInformation(
                    "Seeded development merchant '{Code}' (id {Id}) with X-Api-Key '{ApiKey}'. Sign requests with the configured SigningSecret.",
                    normalisedCode, merchant.Id, seed.ApiKey);
            else
                logger.LogInformation("Dev merchant '{Code}' already present — sign with the configured X-Api-Key/SigningSecret.", normalisedCode);
        }
        catch (DbUpdateException)
        {
            // A concurrent host instance won the unique MerchantCode first. Harmless.
            logger.LogInformation("Dev merchant '{Code}' already present (concurrent seed).", seed.MerchantCode);
        }
        catch (Exception ex)
        {
            // DEV convenience must never brick startup. Usual cause: an un-migrated Merchant schema.
            logger.LogWarning(ex,
                "Dev merchant seeding failed; signed /api/v1 requests will 401 until resolved "
                + "(is the Merchant schema migrated on this database?).");
        }
    }

    /// <summary>
    /// Sets a sample <c>%</c> fee on every active asset for the seeded merchant, so the dev round-trip shows a
    /// real fee split instead of the unpriced zero. Uses the same domain path as production
    /// (<see cref="Domain.Merchant.SetAssetPolicy"/>) — validated, idempotent. Resolved softly: absent an asset
    /// catalog (a Merchant-only test host) or a configured fee, it does nothing.
    /// </summary>
    private async Task ApplySampleFeesAsync(
        AsyncServiceScope scope, Domain.Merchant merchant, DevMerchantSeedOptions seed, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (seed.DepositFeeBps <= 0 && seed.WithdrawalFeeBps <= 0)
            return;

        var assetCatalog = scope.ServiceProvider.GetService<IAssetCatalog>();
        if (assetCatalog is null)
            return;

        var fees = FeeSchedule.Create(BigInteger.Zero, seed.DepositFeeBps, BigInteger.Zero, seed.WithdrawalFeeBps);
        if (fees.IsFailure)
        {
            logger.LogWarning("Dev sample fee skipped: {Error}.", fees.Error!.Message);
            return;
        }

        var assets = await assetCatalog.GetActiveAsync(cancellationToken);
        foreach (var asset in assets)
            merchant.SetAssetPolicy(asset.AssetId, BigInteger.Zero, BigInteger.Zero, null, fees.Value, now);

        if (assets.Count > 0)
            logger.LogInformation(
                "Priced dev merchant '{Code}' at {DepositBps}bps deposit / {WithdrawalBps}bps withdrawal on {AssetCount} asset(s).",
                merchant.MerchantCode, seed.DepositFeeBps, seed.WithdrawalFeeBps, assets.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
