using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
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

            if (await repository.CodeExistsAsync(normalisedCode, cancellationToken))
            {
                logger.LogInformation("Dev merchant '{Code}' already present — sign with the configured X-Api-Key/SigningSecret.", normalisedCode);
                return;
            }

            var merchantResult = Domain.Merchant.Create(seed.MerchantCode, seed.Name, seed.CallbackUrl, timeProvider);
            if (merchantResult.IsFailure)
            {
                logger.LogWarning("Dev merchant seed skipped: {Error}.", merchantResult.Error!.Message);
                return;
            }

            var merchant = merchantResult.Value;
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
            await repository.SaveChangesAsync(cancellationToken);

            // The API key is a public identifier — safe to log. The signing secret is NOT logged (§10).
            logger.LogInformation(
                "Seeded development merchant '{Code}' (id {Id}) with X-Api-Key '{ApiKey}'. Sign requests with the configured SigningSecret.",
                normalisedCode, merchant.Id, seed.ApiKey);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
